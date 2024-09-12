//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.SqlTools.Hosting.Protocol;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.Connection.Contracts;
using Microsoft.SqlTools.ServiceLayer.Connection.ReliableConnection;
using Microsoft.SqlTools.ServiceLayer.ExecutionPlan;
using Microsoft.SqlTools.ServiceLayer.ExecutionPlan.Contracts;
using Microsoft.SqlTools.ServiceLayer.Hosting;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.Contracts.ExecuteRequests;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.Contracts;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.DataStorage;
using Microsoft.SqlTools.ServiceLayer.SqlContext;
using Microsoft.SqlTools.ServiceLayer.Workspace.Contracts;
using Microsoft.SqlTools.ServiceLayer.Workspace;
using Microsoft.SqlTools.Utility;
using TextCopy;

namespace Microsoft.SqlTools.ServiceLayer.QueryExecution
{
    /// <summary>
    /// Service for executing queries
    /// </summary>
    public sealed class QueryExecutionService : IDisposable
    {
        #region Singleton Instance Implementation

        private static readonly Lazy<QueryExecutionService> LazyInstance = new Lazy<QueryExecutionService>(() => new QueryExecutionService());

        /// <summary>
        /// Singleton instance of the query execution service
        /// </summary>
        public static QueryExecutionService Instance => LazyInstance.Value;

        private QueryExecutionService()
        {
            ConnectionService = ConnectionService.Instance;
            WorkspaceService = WorkspaceService<SqlToolsSettings>.Instance;
            Settings = new SqlToolsSettings();
        }

        internal QueryExecutionService(ConnectionService connService, WorkspaceService<SqlToolsSettings> workspaceService)
        {
            ConnectionService = connService;
            WorkspaceService = workspaceService;
            Settings = new SqlToolsSettings();
        }

        #endregion

        #region Properties

        /// <summary>
        /// File factory to be used to create a buffer file for results.
        /// </summary>
        /// <remarks>
        /// Made internal here to allow for overriding in unit testing
        /// </remarks>
        internal IFileStreamFactory BufferFileStreamFactory;

        /// <summary>
        /// File factory to be used to create a buffer file for results
        /// </summary>
        private IFileStreamFactory BufferFileFactory
        {
            get
            {
                BufferFileStreamFactory ??= new ServiceBufferFileStreamFactory
                {
                    QueryExecutionSettings = Settings.QueryExecutionSettings
                };
                return BufferFileStreamFactory;
            }
        }

        /// <summary>
        /// File factory to be used to create CSV files from result sets. Set to internal in order
        /// to allow overriding in unit testing
        /// </summary>
        internal IFileStreamFactory CsvFileFactory { get; set; }

        /// <summary>
        /// File factory to be used to create Excel files from result sets. Set to internal in order
        /// to allow overriding in unit testing
        /// </summary>
        internal IFileStreamFactory ExcelFileFactory { get; set; }

        /// <summary>
        /// File factory to be used to create JSON files from result sets. Set to internal in order
        /// to allow overriding in unit testing
        /// </summary>
        internal IFileStreamFactory JsonFileFactory { get; set; }

        /// <summary>
        /// File factory to be used to create Markdown files from result sets.
        /// </summary>
        /// <remarks>Internal to allow overriding in unit testing.</remarks>
        internal IFileStreamFactory? MarkdownFileFactory { get; set; }

        /// <summary>
        /// File factory to be used to create XML files from result sets. Set to internal in order
        /// to allow overriding in unit testing
        /// </summary>
        internal IFileStreamFactory XmlFileFactory { get; set; }

        /// <summary>
        /// The collection of active queries
        /// </summary>
        internal ConcurrentDictionary<string, Query> ActiveQueries => queries.Value;

        /// <summary>
        /// The collection of query execution options
        /// </summary>
        internal ConcurrentDictionary<string, QueryExecutionSettings> ActiveQueryExecutionSettings => queryExecutionSettings.Value;

        /// <summary>
        /// The collection of query session execution options tracking flags
        /// </summary>
        internal ConcurrentDictionary<string, bool> QuerySessionSettingsApplied => querySessionSettingsApplied.Value;
        
        /// <summary>
        /// Internal task for testability
        /// </summary>
        internal Task WorkTask { get; private set; }

        /// <summary>
        /// Instance of the connection service, used to get the connection info for a given owner URI
        /// </summary>
        private ConnectionService ConnectionService { get; }

        private WorkspaceService<SqlToolsSettings> WorkspaceService { get; }

        /// <summary>
        /// Internal storage of active queries, lazily constructed as a threadsafe dictionary
        /// </summary>
        private readonly Lazy<ConcurrentDictionary<string, Query>> queries =
            new Lazy<ConcurrentDictionary<string, Query>>(() => new ConcurrentDictionary<string, Query>());

        /// <summary>
        /// Internal storage of active query settings
        /// </summary>
        private readonly Lazy<ConcurrentDictionary<string, QueryExecutionSettings>> queryExecutionSettings =
            new Lazy<ConcurrentDictionary<string, QueryExecutionSettings>>(() => new ConcurrentDictionary<string, QueryExecutionSettings>());

        /// <summary>
        /// Internal storage of tracking flags for whether query sessions settings have been applied
        /// </summary>
        private readonly Lazy<ConcurrentDictionary<string, bool>> querySessionSettingsApplied =
            new Lazy<ConcurrentDictionary<string, bool>>(() => new ConcurrentDictionary<string, bool>());

        /// <summary>
        /// Settings that will be used to execute queries. Internal for unit testing
        /// </summary>
        internal SqlToolsSettings Settings { get; set; }

        /// <summary>
        /// Holds a map from the simple execute unique GUID and the underlying task that is being ran
        /// </summary>
        private readonly Lazy<ConcurrentDictionary<string, Task>> simpleExecuteRequests =
            new Lazy<ConcurrentDictionary<string, Task>>(() => new ConcurrentDictionary<string, Task>());

        /// <summary>
        /// Holds a map from the simple execute unique GUID and the underlying task that is being ran
        /// </summary>
        internal ConcurrentDictionary<string, Task> ActiveSimpleExecuteRequests => simpleExecuteRequests.Value;

        #endregion

        /// <summary>
        /// Initializes the service with the service host, registers request handlers and shutdown
        /// event handler.
        /// </summary>
        /// <param name="serviceHost">The service host instance to register with</param>
        public void InitializeService(ServiceHost serviceHost)
        {
            // Register handlers for requests
            serviceHost.SetRequestHandler(ExecuteDocumentSelectionRequest.Type, HandleExecuteRequest, true);
            serviceHost.SetRequestHandler(ExecuteDocumentStatementRequest.Type, HandleExecuteRequest, true);
            serviceHost.SetRequestHandler(ExecuteStringRequest.Type, HandleExecuteRequest, true);
            // Parallel Message processing must be disabled for Subset Request, as the requests must be adhered to in order of when they arrive.
            // Executing in parallel can lead to randomly ordered results, for this reason we should keep it false.
            serviceHost.SetRequestHandler(SubsetRequest.Type, HandleResultSubsetRequest, false);
            serviceHost.SetRequestHandler(QueryDisposeRequest.Type, HandleDisposeRequest, true);
            serviceHost.SetRequestHandler(QueryCancelRequest.Type, HandleCancelRequest, true);
            serviceHost.SetEventHandler(ConnectionUriChangedNotification.Type, HandleConnectionUriChangedNotification);
            serviceHost.SetRequestHandler(SaveResultsAsCsvRequest.Type, HandleSaveResultsAsCsvRequest, true);
            serviceHost.SetRequestHandler(SaveResultsAsExcelRequest.Type, HandleSaveResultsAsExcelRequest, true);
            serviceHost.SetRequestHandler(SaveResultsAsJsonRequest.Type, HandleSaveResultsAsJsonRequest, true);
            serviceHost.SetRequestHandler(SaveResultsAsMarkdownRequest.Type, this.HandleSaveResultsAsMarkdownRequest, true);
            serviceHost.SetRequestHandler(SaveResultsAsXmlRequest.Type, HandleSaveResultsAsXmlRequest, true);
            serviceHost.SetRequestHandler(QueryExecutionPlanRequest.Type, HandleExecutionPlanRequest, true);
            serviceHost.SetRequestHandler(SimpleExecuteRequest.Type, HandleSimpleExecuteRequest, true);
            serviceHost.SetRequestHandler(QueryExecutionOptionsRequest.Type, HandleQueryExecutionOptionsRequest, true);
            serviceHost.SetRequestHandler(CopyResultsRequest.Type, HandleCopyResultsRequest, true);

            // Register the file open update handler
            WorkspaceService<SqlToolsSettings>.Instance.RegisterTextDocCloseCallback(HandleDidCloseTextDocumentNotification);

            // Register handler for shutdown event
            serviceHost.RegisterShutdownTask((shutdownParams, requestContext) =>
            {
                Dispose();
                return Task.FromResult(0);
            });

            // Register a handler for when the configuration changes
            WorkspaceService.RegisterConfigChangeCallback(UpdateSettings);

            // Register a callback for when a connection is created
            ConnectionService.RegisterOnConnectionTask(OnNewConnection);
        }

        #region Request Handlers

        /// <summary>
        /// Handles request to execute a selection of a document in the workspace service
        /// </summary>
        internal Task HandleExecuteRequest(ExecuteRequestParamsBase executeParams,
            RequestContext<ExecuteRequestResult> requestContext)
        {
            // Setup actions to perform upon successful start and on failure to start
            Func<Query, Task<bool>> queryCreateSuccessAction = async q =>
            {
                await requestContext.SendResult(new ExecuteRequestResult());
                Logger.Stop($"Response for Query: '{executeParams.OwnerUri} sent. Query Complete!");
                return true;
            };
            Func<string, Task> queryCreateFailureAction = message =>
            {
                Logger.Warning($"Failed to create Query: '{executeParams.OwnerUri}. Message: '{message}' Complete!");
                return requestContext.SendError(message);
            };

            // Use the internal handler to launch the query
            WorkTask = Task.Run(async () =>
            {
                await InterServiceExecuteQuery(
                    executeParams,
                    null,
                    requestContext,
                    queryCreateSuccessAction,
                    queryCreateFailureAction,
                    null,
                    null,
                    isQueryEditor(executeParams.OwnerUri));
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles a request to execute a string and return the result
        /// </summary>
        internal async Task HandleSimpleExecuteRequest(SimpleExecuteParams executeParams,
            RequestContext<SimpleExecuteResult> requestContext)
        {
            string randomUri = Guid.NewGuid().ToString();
            ExecuteStringParams executeStringParams = new ExecuteStringParams
            {
                Query = executeParams.QueryString,
                // generate guid as the owner uri to make sure every query is unique
                OwnerUri = randomUri
            };

            // get connection
            ConnectionInfo connInfo;
            if (!ConnectionService.TryFindConnection(executeParams.OwnerUri, out connInfo))
            {
                await requestContext.SendError(SR.QueryServiceQueryInvalidOwnerUri);
                return;
            }

            ConnectParams connectParams = new ConnectParams
            {
                OwnerUri = randomUri,
                Connection = connInfo.ConnectionDetails,
                Type = ConnectionType.Default
            };

            Task workTask = Task.Run(async () =>
            {
                await ConnectionService.Connect(connectParams);

                ConnectionInfo newConn;
                ConnectionService.TryFindConnection(randomUri, out newConn);

                Func<string, Task> queryCreateFailureAction = message => requestContext.SendError(message);

                ResultOnlyContext<SimpleExecuteResult> newContext = new ResultOnlyContext<SimpleExecuteResult>(requestContext);

                // handle sending event back when the query completes
                Query.QueryAsyncEventHandler queryComplete = async query =>
                {
                    try
                    {
                        // check to make sure any results were recieved
                        if (query.Batches.Length == 0
                            || query.Batches[0].ResultSets.Count == 0)
                        {
                            await requestContext.SendError(SR.QueryServiceResultSetHasNoResults);
                            return;
                        }

                        long rowCount = query.Batches[0].ResultSets[0].RowCount;
                        // check to make sure there is a safe amount of rows to load into memory
                        if (rowCount > Int32.MaxValue)
                        {
                            await requestContext.SendError(SR.QueryServiceResultSetTooLarge);
                            return;
                        }

                        SimpleExecuteResult result = new SimpleExecuteResult
                        {
                            RowCount = rowCount,
                            ColumnInfo = query.Batches[0].ResultSets[0].Columns,
                            Rows = new DbCellValue[0][]
                        };

                        if (rowCount > 0)
                        {
                            SubsetParams subsetRequestParams = new SubsetParams
                            {
                                OwnerUri = randomUri,
                                BatchIndex = 0,
                                ResultSetIndex = 0,
                                RowsStartIndex = 0,
                                RowsCount = Convert.ToInt32(rowCount)
                            };
                            // get the data to send back
                            ResultSetSubset subset = await InterServiceResultSubset(subsetRequestParams);
                            result.Rows = subset.Rows;
                        }
                        await requestContext.SendResult(result);
                    }
                    finally
                    {
                        Query removedQuery;
                        Task removedTask;
                        // remove the active query since we are done with it
                        ActiveQueries.TryRemove(randomUri, out removedQuery);
                        ActiveSimpleExecuteRequests.TryRemove(randomUri, out removedTask);
                        ConnectionService.Disconnect(new DisconnectParams()
                        {
                            OwnerUri = randomUri,
                            Type = null
                        });
                    }
                };

                // handle sending error back when query fails
                Query.QueryAsyncErrorEventHandler queryFail = async (q, e) =>
                {
                    await requestContext.SendError(e);
                };

                await InterServiceExecuteQuery(executeStringParams, newConn, newContext, null, queryCreateFailureAction, queryComplete, queryFail);
            });

            ActiveSimpleExecuteRequests.TryAdd(randomUri, workTask);
        }


        /// <summary>
        /// Handles a request to change the uri associated with an active query and connection info.
        /// </summary>
        internal Task HandleConnectionUriChangedNotification(ConnectionUriChangedParams changeUriParams,
            EventContext eventContext)
        {
            try
            {
                string originalOwnerUri = changeUriParams.OriginalOwnerUri;
                string newOwnerUri = changeUriParams.NewOwnerUri;
                ConnectionService.ReplaceUri(originalOwnerUri, newOwnerUri);
                // Attempt to load the query
                Query query;
                if (!ActiveQueries.TryRemove(originalOwnerUri, out query))
                {
                    throw new Exception("Uri: " + originalOwnerUri + " is not associated with an active query.");
                }
                query.ConnectionOwnerURI = newOwnerUri;
                ActiveQueries.TryAdd(newOwnerUri, query);

                // Update the session query execution options applied map
                bool settingsApplied;
                if (!QuerySessionSettingsApplied.TryRemove(originalOwnerUri, out settingsApplied))
                {
                    throw new Exception("Uri: " + originalOwnerUri + " is not associated with an active query settings.");
                }
                QuerySessionSettingsApplied.TryAdd(newOwnerUri, settingsApplied);

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error("Error encountered " + ex.ToString());
                return Task.FromException(ex);
            }
        }

        /// <summary>
        /// Handles a request to get a subset of the results of this query
        /// </summary>
        internal async Task HandleResultSubsetRequest(SubsetParams subsetParams,
            RequestContext<SubsetResult> requestContext)
        {
            ResultSetSubset subset = await InterServiceResultSubset(subsetParams);
            var result = new SubsetResult
            {
                ResultSubset = subset
            };
            await requestContext.SendResult(result);
            Logger.Stop($"Done Handler for Subset request with for Query:'{subsetParams.OwnerUri}', Batch:'{subsetParams.BatchIndex}', ResultSetIndex:'{subsetParams.ResultSetIndex}', RowsStartIndex'{subsetParams.RowsStartIndex}', Requested RowsCount:'{subsetParams.RowsCount}'\r\n\t\t with subset response of:[ RowCount:'{subset.RowCount}', Rows array of length:'{subset.Rows.Length}']");
        }


        /// <summary>
        /// Handles a request to set query execution options
        /// </summary>
        internal async Task HandleQueryExecutionOptionsRequest(QueryExecutionOptionsParams queryExecutionOptionsParams,
            RequestContext<bool> requestContext)
        {
            string uri = queryExecutionOptionsParams.OwnerUri;
            if (ActiveQueryExecutionSettings.ContainsKey(uri))
            {
                QueryExecutionSettings settings;
                ActiveQueryExecutionSettings.TryRemove(uri, out settings);
            }

            ActiveQueryExecutionSettings.TryAdd(uri, queryExecutionOptionsParams.Options);

            await requestContext.SendResult(true);
        }

        /// <summary>
        /// Handles a request to get an execution plan
        /// </summary>
        internal async Task HandleExecutionPlanRequest(QueryExecutionPlanParams planParams,
            RequestContext<QueryExecutionPlanResult> requestContext)
        {
            // Attempt to load the query
            Query query;
            if (!ActiveQueries.TryGetValue(planParams.OwnerUri, out query))
            {
                await requestContext.SendError(SR.QueryServiceRequestsNoQuery);
                return;
            }

            // Retrieve the requested execution plan and return it
            var result = new QueryExecutionPlanResult
            {
                ExecutionPlan = await query.GetExecutionPlan(planParams.BatchIndex, planParams.ResultSetIndex)
            };
            await requestContext.SendResult(result);
        }

        /// <summary>
        /// Handles a request to dispose of this query
        /// </summary>
        internal async Task HandleDisposeRequest(QueryDisposeParams disposeParams,
            RequestContext<QueryDisposeResult> requestContext)
        {
            // Setup action for success and failure
            Func<Task> successAction = () => requestContext.SendResult(new QueryDisposeResult());
            Func<string, Task> failureAction = message => requestContext.SendError(message);

            // Use the inter-service dispose functionality
            await InterServiceDisposeQuery(disposeParams.OwnerUri, successAction, failureAction);
        }

        /// <summary>
        /// Handles a request to cancel this query if it is in progress
        /// </summary>
        internal async Task HandleCancelRequest(QueryCancelParams cancelParams,
            RequestContext<QueryCancelResult> requestContext)
        {
            try
            {
                // Attempt to find the query for the owner uri
                Query result;
                if (!ActiveQueries.TryGetValue(cancelParams.OwnerUri, out result))
                {
                    await requestContext.SendResult(new QueryCancelResult
                    {
                        Messages = SR.QueryServiceRequestsNoQuery
                    });
                    return;
                }

                // Cancel the query and send a success message
                result.Cancel();
                await requestContext.SendResult(new QueryCancelResult());
            }
            catch (InvalidOperationException e)
            {
                // If this exception occurred, we most likely were trying to cancel a completed query
                await requestContext.SendResult(new QueryCancelResult
                {
                    Messages = e.Message
                });
            }
        }

        /// <summary>
        /// Process request to save a resultSet to a file in CSV format
        /// </summary>
        internal async Task HandleSaveResultsAsCsvRequest(SaveResultsAsCsvRequestParams saveParams,
            RequestContext<SaveResultRequestResult> requestContext)
        {
            // Use the default CSV file factory if we haven't overridden it
            IFileStreamFactory csvFactory = CsvFileFactory ?? new SaveAsCsvFileStreamFactory
            {
                SaveRequestParams = saveParams,
                QueryExecutionSettings = Settings.QueryExecutionSettings
            };
            await SaveResultsHelper(saveParams, requestContext, csvFactory);
        }

        /// <summary>
        /// Process request to save a resultSet to a file in Excel format
        /// </summary>
        internal async Task HandleSaveResultsAsExcelRequest(SaveResultsAsExcelRequestParams saveParams,
            RequestContext<SaveResultRequestResult> requestContext)
        {
            // Use the default Excel file factory if we haven't overridden it
            IFileStreamFactory excelFactory = ExcelFileFactory ?? new SaveAsExcelFileStreamFactory
            {
                SaveRequestParams = saveParams,
                QueryExecutionSettings = Settings.QueryExecutionSettings
            };
            await SaveResultsHelper(saveParams, requestContext, excelFactory);
        }

        /// <summary>
        /// Process request to save a resultSet to a file in JSON format
        /// </summary>
        internal async Task HandleSaveResultsAsJsonRequest(SaveResultsAsJsonRequestParams saveParams,
            RequestContext<SaveResultRequestResult> requestContext)
        {
            // Use the default JSON file factory if we haven't overridden it
            IFileStreamFactory jsonFactory = JsonFileFactory ?? new SaveAsJsonFileStreamFactory
            {
                SaveRequestParams = saveParams,
                QueryExecutionSettings = Settings.QueryExecutionSettings
            };
            await SaveResultsHelper(saveParams, requestContext, jsonFactory);
        }

        /// <summary>
        /// Processes a request to save a result set to a file in Markdown format.
        /// </summary>
        /// <param name="saveParams">Parameters for the request</param>
        /// <param name="requestContext">Context of the request</param>
        internal async Task HandleSaveResultsAsMarkdownRequest(
            SaveResultsAsMarkdownRequestParams saveParams,
            RequestContext<SaveResultRequestResult> requestContext)
        {
            // Use the default markdown file factory if we haven't overridden it
            IFileStreamFactory markdownFactory = this.MarkdownFileFactory ??
                                                 new SaveAsMarkdownFileStreamFactory(saveParams)
                                                 {
                                                     QueryExecutionSettings = this.Settings.QueryExecutionSettings,
                                                 };

            await this.SaveResultsHelper(saveParams, requestContext, markdownFactory);
        }

        /// <summary>
        /// Process request to save a resultSet to a file in XML format
        /// </summary>
        internal async Task HandleSaveResultsAsXmlRequest(SaveResultsAsXmlRequestParams saveParams,
            RequestContext<SaveResultRequestResult> requestContext)
        {
            // Use the default XML file factory if we haven't overridden it
            IFileStreamFactory xmlFactory = XmlFileFactory ?? new SaveAsXmlFileStreamFactory
            {
                SaveRequestParams = saveParams,
                QueryExecutionSettings = Settings.QueryExecutionSettings
            };
            await SaveResultsHelper(saveParams, requestContext, xmlFactory);
        }

        #endregion

        #region Inter-Service API Handlers

        /// <summary>
        /// Query execution meant to be called from another service. Utilizes callbacks to allow
        /// custom actions to be taken upon creation of query and failure to create query.
        /// </summary>
        /// <param name="executeParams">Parameters for execution</param>
        /// <param name="connInfo">Connection Info to use; will try and get the connection from owneruri if not provided</param>
        /// <param name="queryEventSender">Event sender that will send progressive events during execution of the query</param>
        /// <param name="queryCreateSuccessFunc">
        /// Callback for when query has been created successfully. If result is <c>true</c>, query
        /// will be executed asynchronously. If result is <c>false</c>, query will be disposed. May
        /// be <c>null</c>
        /// </param>
        /// <param name="queryCreateFailFunc">
        /// Callback for when query failed to be created successfully. Error message is provided.
        /// May be <c>null</c>.
        /// </param>
        /// <param name="querySuccessFunc">
        /// Callback to call when query has completed execution successfully. May be <c>null</c>.
        /// </param>
        /// <param name="queryFailureFunc">
        /// Callback to call when query has completed execution with errors. May be <c>null</c>.
        /// </param>
        public async Task InterServiceExecuteQuery(ExecuteRequestParamsBase executeParams,
            ConnectionInfo connInfo,
            IEventSender queryEventSender,
            Func<Query, Task<bool>> queryCreateSuccessFunc,
            Func<string, Task> queryCreateFailFunc,
            Query.QueryAsyncEventHandler querySuccessFunc,
            Query.QueryAsyncErrorEventHandler queryFailureFunc,
            bool applyExecutionSettings = false)
        {
            Validate.IsNotNull(nameof(executeParams), executeParams);
            Validate.IsNotNull(nameof(queryEventSender), queryEventSender);

            Query newQuery;
            try
            {
                // Get a new active query
                newQuery = CreateQuery(executeParams, connInfo, applyExecutionSettings);
                if (queryCreateSuccessFunc != null && !await queryCreateSuccessFunc(newQuery))
                {
                    // The callback doesn't want us to continue, for some reason
                    // It's ok if we leave the query behind in the active query list, the next call
                    // to execute will replace it.
                    newQuery.Dispose();
                    return;
                }
            }
            catch (Exception e)
            {
                // Call the failure callback if it was provided
                if (queryCreateFailFunc != null)
                {
                    await queryCreateFailFunc(e.Message);
                }
                return;
            }

            if (connInfo == null)
            {
                ConnectionService.TryFindConnection(executeParams.OwnerUri, out connInfo);
            }

            bool sessionSettingsApplied;
            if (!this.QuerySessionSettingsApplied.TryGetValue(connInfo.OwnerUri, out sessionSettingsApplied))
            {
                sessionSettingsApplied = false;
            }

            if (!sessionSettingsApplied)
            {
                ApplySessionQueryExecutionOptions(connInfo, newQuery.Settings);
                this.QuerySessionSettingsApplied.AddOrUpdate(connInfo.OwnerUri, true, (key, oldValue) => true);
            }

            // Execute the query asynchronously
            ExecuteAndCompleteQuery(executeParams.OwnerUri, newQuery, queryEventSender, querySuccessFunc, queryFailureFunc);
        }

        /// <summary>
        /// Query disposal meant to be called from another service. Utilizes callbacks to allow
        /// custom actions to be performed on success or failure.
        /// </summary>
        /// <param name="ownerUri">The identifier of the query to be disposed</param>
        /// <param name="successAction">Action to perform on success</param>
        /// <param name="failureAction">Action to perform on failure</param>
        public async Task InterServiceDisposeQuery(string ownerUri, Func<Task> successAction,
            Func<string, Task> failureAction)
        {
            Validate.IsNotNull(nameof(successAction), successAction);
            Validate.IsNotNull(nameof(failureAction), failureAction);

            try
            {
                // Attempt to remove the query for the owner uri
                Query result;
                if (!ActiveQueries.TryRemove(ownerUri, out result))
                {
                    await failureAction(SR.QueryServiceRequestsNoQuery);
                    return;
                }

                // Cleanup the query
                result.Dispose();

                // Success
                await successAction();
            }
            catch (Exception e)
            {
                await failureAction(e.Message);
            }
        }

        /// <summary>
        /// Retrieves the requested subset of rows from the requested result set. Intended to be
        /// called by another service.
        /// </summary>
        /// <param name="subsetParams">Parameters for the subset to retrieve</param>
        /// <returns>The requested subset</returns>
        /// <exception cref="ArgumentOutOfRangeException">The requested query does not exist</exception>
        public async Task<ResultSetSubset> InterServiceResultSubset(SubsetParams subsetParams)
        {
            Validate.IsNotNullOrEmptyString(nameof(subsetParams.OwnerUri), subsetParams.OwnerUri);

            // Attempt to load the query
            Query query;
            if (!ActiveQueries.TryGetValue(subsetParams.OwnerUri, out query))
            {
                throw new ArgumentOutOfRangeException(SR.QueryServiceRequestsNoQuery);
            }

            // Retrieve the requested subset and return it
            return await query.GetSubset(subsetParams.BatchIndex, subsetParams.ResultSetIndex,
                subsetParams.RowsStartIndex, subsetParams.RowsCount);
        }

        /// <summary>
        /// Handle the file open notification
        /// </summary>
        /// <param name="scriptFile"></param>
        /// <param name="eventContext"></param>
        /// <returns></returns>
        public Task HandleDidCloseTextDocumentNotification(
            string uri,
            ScriptFile scriptFile,
            EventContext eventContext)
        {
            try
            {
                // remove any query execution settings when an editor is closed
                if (this.ActiveQueryExecutionSettings.ContainsKey(uri))
                {
                    QueryExecutionSettings settings;
                    this.ActiveQueryExecutionSettings.TryRemove(uri, out settings);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unknown error " + ex.ToString());
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs UpdateLanguageServiceOnConnection as a background task
        /// </summary>
        /// <param name="info">Connection Info</param>
        /// <returns></returns>
        private Task OnNewConnection(ConnectionInfo info)
        {
            this.QuerySessionSettingsApplied.AddOrUpdate(info.OwnerUri, false, (key, oldValue) => false);
    
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles the copy results.
        /// </summary>
        internal async Task HandleCopyResultsRequest(CopyResultsRequestParams requestParams, RequestContext<CopyResultsRequestResult> requestContext)
        {
            var valueSeparator = "\t";
            var columnRanges = this.MergeRanges(requestParams.Selections.Select(selection => new Range() { Start = selection.FromColumn, End = selection.ToColumn }).ToList());
            var rowRanges = this.MergeRanges(requestParams.Selections.Select(selection => new Range() { Start = selection.FromRow, End = selection.ToRow }).ToList());
            var lastColumnIndex = columnRanges.Last().End;
            var lastRowIndex = rowRanges.Last().End;
            var builder = new StringBuilder();
            var pageSize = 200;

            // We need to respect IncludeHeaders from parameters instead of getting the config value as ADS can explicitly ask for headers
            if (requestParams.IncludeHeaders)
            {
                Validate.IsNotNullOrEmptyString(nameof(requestParams.OwnerUri), requestParams.OwnerUri);
                Query query;
                if (!ActiveQueries.TryGetValue(requestParams.OwnerUri, out query))
                {
                    throw new ArgumentOutOfRangeException(SR.QueryServiceRequestsNoQuery);
                }
                var columnNames = query.GetColumnNames(requestParams.BatchIndex, requestParams.ResultSetIndex);
                var selectedColumns = new List<string>();
                for (int i = 0; i < columnNames.Count; i++)
                {
                    if (columnRanges.Any(range => i >= range.Start && i <= range.End))
                    {
                        selectedColumns.Add(columnNames[i]);
                    }
                }
                builder.Append(string.Join(valueSeparator, selectedColumns));
                builder.Append(Environment.NewLine);
            }

            for (int rowRangeIndex = 0; rowRangeIndex < rowRanges.Count; rowRangeIndex++)
            {
                var rowRange = rowRanges[rowRangeIndex];
                var pageStartRowIndex = rowRange.Start;
                // Read the rows in batches to avoid holding all rows in memory
                do
                {
                    var rowsToFetch = Math.Min(pageSize, rowRange.End - pageStartRowIndex + 1);
                    ResultSetSubset subset = await InterServiceResultSubset(new SubsetParams()
                    {
                        OwnerUri = requestParams.OwnerUri,
                        ResultSetIndex = requestParams.ResultSetIndex,
                        BatchIndex = requestParams.BatchIndex,
                        RowsStartIndex = pageStartRowIndex,
                        RowsCount = rowsToFetch
                    });
                    for (int rowIndex = 0; rowIndex < subset.Rows.Length; rowIndex++)
                    {
                        var row = subset.Rows[rowIndex];
                        for (int columnRangeIndex = 0; columnRangeIndex < columnRanges.Count; columnRangeIndex++)
                        {
                            var columnRange = columnRanges[columnRangeIndex];
                            for (int columnIndex = columnRange.Start; columnIndex <= columnRange.End; columnIndex++)
                            {
                                if (requestParams.Selections.Any(selection =>
                                selection.FromRow <= rowIndex + rowRange.Start &&
                                selection.ToRow >= rowIndex + rowRange.Start &&
                                selection.FromColumn <= columnIndex &&
                                selection.ToColumn >= columnIndex))
                                {
                                    if (row != null)
                                    {
                                        if (row[columnIndex] != null && row[columnIndex].DisplayValue != null)
                                        {
                                            builder.Append(Settings?.QueryEditorSettings?.Results?.CopyRemoveNewLine ?? true ? row[columnIndex]?.DisplayValue?.ReplaceLineEndings(" ") : row[columnIndex]?.DisplayValue);
                                        }
                                        else
                                        {
                                            // Temporary logging to investigate NRE, can be removed in future.
                                            Logger.Verbose($"Value at row: {rowIndex} and column: {columnIndex} found null.");
                                        }
                                    }
                                    else
                                    {
                                        // Temporary logging to investigate NRE, can be removed in future.
                                        Logger.Verbose($"Row not found at rowIndex: {rowIndex}, rowRange: {rowRange}, rowRangeIndex {rowRangeIndex}");
                                    }
                                }
                                if (columnIndex != lastColumnIndex)
                                {
                                    builder.Append(valueSeparator);
                                }
                            }
                        }
                        // Add line break if this is not the last row in all selections.
                        if (rowIndex + pageStartRowIndex != lastRowIndex && (!StringBuilderEndsWith(builder, Environment.NewLine) || (!Settings?.QueryEditorSettings?.Results?.SkipNewLineAfterTrailingLineBreak ?? true)))
                        {
                            builder.Append(Environment.NewLine);
                        }
                    }
                    pageStartRowIndex += rowsToFetch;
                } while (pageStartRowIndex < rowRange.End);
            }
            await ClipboardService.SetTextAsync(builder.ToString());
            await requestContext.SendResult(new CopyResultsRequestResult());
        }

        #endregion

        #region Private Helpers

        private bool StringBuilderEndsWith(StringBuilder sb, string target)
        {
            if (sb.Length < target.Length)
            {
                return false;
            }

            // Calling ToString like this only converts the last few characters of the StringBuilder to a string
            return sb.ToString(sb.Length - target.Length, target.Length).EndsWith(target);
        }

        private Query CreateQuery(
            ExecuteRequestParamsBase executeParams,
            ConnectionInfo connInfo,
            bool applyExecutionSettings)
        {
            // Attempt to get the connection for the editor
            ConnectionInfo connectionInfo;
            if (connInfo != null)
            {
                connectionInfo = connInfo;
            }
            else if (!ConnectionService.TryFindConnection(executeParams.OwnerUri, out connectionInfo))
            {
                throw new ArgumentOutOfRangeException(nameof(executeParams.OwnerUri), SR.QueryServiceQueryInvalidOwnerUri);
            }

            // Attempt to clean out any old query on the owner URI
            Query oldQuery;
            // DevNote:
            //    if any oldQuery exists on the executeParams.OwnerUri but it has not yet executed,
            //    then shouldn't we cancel and clean out that query since we are about to create a new query object on the current OwnerUri.
            //
            if (ActiveQueries.TryGetValue(executeParams.OwnerUri, out oldQuery) && (oldQuery.HasExecuted || oldQuery.HasCancelled || oldQuery.HasErrored))
            {
                oldQuery.Dispose();
                ActiveQueries.TryRemove(executeParams.OwnerUri, out oldQuery);
            }

            // check if there are active query execution settings for the editor, otherwise, use the global settings
            QueryExecutionSettings settings;
            if (this.ActiveQueryExecutionSettings.TryGetValue(executeParams.OwnerUri, out settings))
            {
                // special-case handling for query plan options to maintain compat with query execution API parameters
                // the logic is that if either the query execute API parameters or the active query setttings
                // request a plan then enable the query option
                ExecutionPlanOptions executionPlanOptions = executeParams.ExecutionPlanOptions;
                if (settings.IncludeActualExecutionPlanXml)
                {
                    executionPlanOptions.IncludeActualExecutionPlanXml = settings.IncludeActualExecutionPlanXml;
                }
                if (settings.IncludeEstimatedExecutionPlanXml)
                {
                    executionPlanOptions.IncludeEstimatedExecutionPlanXml = settings.IncludeEstimatedExecutionPlanXml;
                }
                settings.ExecutionPlanOptions = executionPlanOptions;
            }
            else
            {
                settings = Settings.QueryExecutionSettings;
                settings.ExecutionPlanOptions = executeParams.ExecutionPlanOptions;
            }

            // If we can't add the query now, it's assumed the query is in progress
            Query newQuery = new Query(
                GetSqlText(executeParams),
                connectionInfo,
                settings,
                BufferFileFactory,
                executeParams.GetFullColumnSchema,
                applyExecutionSettings);

            if (!ActiveQueries.TryAdd(executeParams.OwnerUri, newQuery))
            {
                newQuery.Dispose();
                throw new InvalidOperationException(SR.QueryServiceQueryInProgress);
            }

            Logger.Information($"Query object for URI:'{executeParams.OwnerUri}' created");
            return newQuery;
        }

        private static void ExecuteAndCompleteQuery(string ownerUri, Query query,
            IEventSender eventSender,
            Query.QueryAsyncEventHandler querySuccessCallback,
            Query.QueryAsyncErrorEventHandler queryFailureCallback)
        {
            // Setup the callback to send the complete event
            Query.QueryAsyncEventHandler completeCallback = async q =>
            {
                // Send back the results
                QueryCompleteParams eventParams = new QueryCompleteParams
                {
                    OwnerUri = ownerUri,
                    BatchSummaries = q.BatchSummaries,
                    ServerConnectionId = q.ServerConnectionId,
                };

                Logger.Information($"Query:'{ownerUri}' completed");
                await eventSender.SendEvent(QueryCompleteEvent.Type, eventParams);
            };

            // Setup the callback to send the failure event
            Query.QueryAsyncErrorEventHandler failureCallback = async (q, e) =>
            {
                // Send back the results
                QueryCompleteParams eventParams = new QueryCompleteParams
                {
                    OwnerUri = ownerUri,
                    BatchSummaries = q.BatchSummaries,
                    ServerConnectionId = q.ServerConnectionId,
                };

                Logger.Error($"Query:'{ownerUri}' failed");
                await eventSender.SendEvent(QueryCompleteEvent.Type, eventParams);
            };
            query.QueryCompleted += completeCallback;
            query.QueryFailed += failureCallback;

            // Add the callbacks that were provided by the caller
            // If they're null, that's no problem
            query.QueryCompleted += querySuccessCallback;
            query.QueryFailed += queryFailureCallback;

            // Setup the batch callbacks
            Batch.BatchAsyncEventHandler batchStartCallback = async b =>
            {
                BatchEventParams eventParams = new BatchEventParams
                {
                    BatchSummary = b.Summary,
                    OwnerUri = ownerUri
                };

                Logger.Information($"Batch:'{b.Summary}' on Query:'{ownerUri}' started");
                await eventSender.SendEvent(BatchStartEvent.Type, eventParams);
            };
            query.BatchStarted += batchStartCallback;

            Batch.BatchAsyncEventHandler batchCompleteCallback = async b =>
            {
                BatchEventParams eventParams = new BatchEventParams
                {
                    BatchSummary = b.Summary,
                    OwnerUri = ownerUri
                };

                Logger.Information($"Batch:'{b.Summary}' on Query:'{ownerUri}' completed");
                await eventSender.SendEvent(BatchCompleteEvent.Type, eventParams);
            };
            query.BatchCompleted += batchCompleteCallback;

            Batch.BatchAsyncMessageHandler batchMessageCallback = async m =>
            {
                MessageParams eventParams = new MessageParams
                {
                    Message = m,
                    OwnerUri = ownerUri
                };

                Logger.Information($"Message generated on Query:'{ownerUri}' :'{m}'");
                await eventSender.SendEvent(MessageEvent.Type, eventParams);
            };
            query.BatchMessageSent += batchMessageCallback;

            // Setup the ResultSet available callback
            ResultSet.ResultSetAsyncEventHandler resultAvailableCallback = async r =>
            {
                ResultSetAvailableEventParams eventParams = new ResultSetAvailableEventParams
                {
                    ResultSetSummary = r.Summary,
                    OwnerUri = ownerUri
                };

                Logger.Information($"Result:'{r.Summary} on Query:'{ownerUri}' is available");
                await eventSender.SendEvent(ResultSetAvailableEvent.Type, eventParams);
            };
            query.ResultSetAvailable += resultAvailableCallback;

            // Setup the ResultSet updated callback
            ResultSet.ResultSetAsyncEventHandler resultUpdatedCallback = async r =>
            {

                //Generating and sending an execution plan graphs if it is requested.
                List<ExecutionPlanGraph> plans = null;
                string planErrors = "";
                if (r.Summary.Complete && r.Summary.SpecialAction.ExpectYukonXMLShowPlan && r.RowCount == 1 && r.GetRow(0)[0] != null)
                {
                    var xmlString = r.GetRow(0)[0].DisplayValue;
                    try
                    {
                        plans = ExecutionPlanGraphUtils.CreateShowPlanGraph(xmlString, Path.GetFileName(ownerUri));
                    }
                    catch (Exception ex)
                    {
                        // In case of error we are sending an empty execution plan graph with the error message.
                        Logger.Error(String.Format("Failed to generate show plan graph{0}{1}", Environment.NewLine, ex.Message));
                        planErrors = ex.Message;
                    }

                }
                ResultSetUpdatedEventParams eventParams = new ResultSetUpdatedEventParams
                {
                    ResultSetSummary = r.Summary,
                    OwnerUri = ownerUri,
                    ExecutionPlans = plans,
                    ExecutionPlanErrorMessage = planErrors
                };

                await eventSender.SendEvent(ResultSetUpdatedEvent.Type, eventParams);
            };
            query.ResultSetUpdated += resultUpdatedCallback;

            // Setup the ResultSet completion callback
            ResultSet.ResultSetAsyncEventHandler resultCompleteCallback = async r =>
            {
                ResultSetCompleteEventParams eventParams = new ResultSetCompleteEventParams
                {
                    ResultSetSummary = r.Summary,
                    OwnerUri = ownerUri
                };

                Logger.Information($"Result:'{r.Summary} on Query:'{ownerUri}' is complete");
                await eventSender.SendEvent(ResultSetCompleteEvent.Type, eventParams);
            };
            query.ResultSetCompleted += resultCompleteCallback;

            // Launch this as an asynchronous task
            query.Execute();
        }

        private async Task SaveResultsHelper(SaveResultsRequestParams saveParams,
            RequestContext<SaveResultRequestResult> requestContext, IFileStreamFactory fileFactory)
        {
            // retrieve query for OwnerUri
            Query query;
            if (!ActiveQueries.TryGetValue(saveParams.OwnerUri, out query))
            {
                await requestContext.SendError(SR.QueryServiceQueryInvalidOwnerUri);
                return;
            }

            //Setup the callback for completion of the save task
            ResultSet.SaveAsAsyncEventHandler successHandler = async parameters =>
            {
                await requestContext.SendResult(new SaveResultRequestResult());
            };
            ResultSet.SaveAsFailureAsyncEventHandler errorHandler = async (parameters, reason) =>
            {
                string message = SR.QueryServiceSaveAsFail(Path.GetFileName(parameters.FilePath), reason);
                await requestContext.SendError(message);
            };

            try
            {
                // Launch the task
                query.SaveAs(saveParams, fileFactory, successHandler, errorHandler);
            }
            catch (Exception e)
            {
                await errorHandler(saveParams, e.Message);
            }
        }

        // Internal for testing purposes
        internal string GetSqlText(ExecuteRequestParamsBase request)
        {
            // This URI doesn't come in escaped - so if it's a file path with reserved characters (such as %)
            // then we'll fail to find it since GetFile expects the URI to be a fully-escaped URI as that's
            // what the document events are sent in as.
            var escapedOwnerUri = Uri.EscapeUriString(request.OwnerUri);
            // If it is a document selection, we'll retrieve the text from the document
            if (request is ExecuteDocumentSelectionParams docRequest)
            {
                return GetSqlTextFromSelectionData(escapedOwnerUri, docRequest.QuerySelection);
            }

            // If it is a document statement, we'll retrieve the text from the document
            if (request is ExecuteDocumentStatementParams stmtRequest)
            {
                return GetSqlStatementAtPosition(escapedOwnerUri, stmtRequest.Line, stmtRequest.Column);
            }

            // If it is an ExecuteStringParams, return the text as is
            if (request is ExecuteStringParams stringRequest)
            {
                return stringRequest.Query;
            }

            // Note, this shouldn't be possible due to inheritance rules
            throw new InvalidCastException("Invalid request type");
        }

        /// <summary>
        /// Return portion of document corresponding to the selection range
        /// </summary>
        internal string GetSqlTextFromSelectionData(string ownerUri, SelectionData selection)
        {
            // Get the document from the parameters
            ScriptFile queryFile = WorkspaceService.Workspace.GetFile(ownerUri);
            if (queryFile == null)
            {
                Logger.Warning($"[GetSqlTextFromSelectionData] Unable to find document with OwnerUri {ownerUri}");
                return string.Empty;
            }
            // If a selection was not provided, use the entire document
            if (selection == null)
            {
                return queryFile.Contents;
            }

            // A selection was provided, so get the lines in the selected range
            string[] queryTextArray = queryFile.GetLinesInRange(
                new BufferRange(
                    new BufferPosition(
                        selection.StartLine + 1,
                        selection.StartColumn + 1
                    ),
                    new BufferPosition(
                        selection.EndLine + 1,
                        selection.EndColumn + 1
                    )
                )
            );
            return string.Join(Environment.NewLine, queryTextArray);
        }

        /// <summary>
        /// Return portion of document corresponding to the statement at the line and column
        /// </summary>
        internal string GetSqlStatementAtPosition(string ownerUri, int line, int column)
        {
            // Get the document from the parameters
            ScriptFile queryFile = WorkspaceService.Workspace.GetFile(ownerUri);
            if (queryFile == null)
            {
                return string.Empty;
            }

            return LanguageServices.LanguageService.Instance.ParseStatementAtPosition(
                queryFile.Contents, line, column);
        }

        /// Internal for testing purposes
        internal Task UpdateSettings(SqlToolsSettings newSettings, SqlToolsSettings oldSettings, EventContext eventContext)
        {
            Settings.QueryExecutionSettings.Update(newSettings.QueryExecutionSettings);
            Settings.QueryEditorSettings.Update(newSettings.QueryEditorSettings);
            return Task.FromResult(0);
        }

        internal class Range
        {
            public int Start { get; set; }
            public int End { get; set; }

            public override string ToString() 
                => $"{nameof(Range)} ({nameof(Start)}: {Start}, {nameof(End)}: {End})";
        }

        internal List<Range> MergeRanges(List<Range> ranges)
        {
            var mergedRanges = new List<Range>();
            ranges.Sort((range1, range2) => (range1.Start - range2.Start));
            foreach (var range in ranges)
            {
                bool merged = false;
                foreach (var mergedRange in mergedRanges)
                {
                    if (range.Start <= mergedRange.End)
                    {
                        mergedRange.End = Math.Max(range.End, mergedRange.End);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                {
                    mergedRanges.Add(range);
                }
            }
            return mergedRanges;
        }

        private async void ApplySessionQueryExecutionOptions(ConnectionInfo connection, QueryExecutionSettings settings)
        {
            QuerySettingsHelper helper = new QuerySettingsHelper(settings);

            StringBuilder sqlBuilder = new StringBuilder(512);

            // append first part of exec options
            sqlBuilder.AppendFormat("{0} {1} {2}",
                helper.SetRowCountString, helper.SetTextSizeString, helper.SetNoCountString);

            if (!connection.IsSqlDW)
            {
                // append second part of exec options
                sqlBuilder.AppendFormat(" {0} {1} {2} {3} {4} {5} {6}",
                                        helper.SetConcatenationNullString,
                                        helper.SetArithAbortString,
                                        helper.SetLockTimeoutString,
                                        helper.SetQueryGovernorCostString,
                                        helper.SetDeadlockPriorityString,
                                        helper.SetTransactionIsolationLevelString,
                                        // We treat XACT_ABORT special in that we don't add anything if the option
                                        // isn't checked. This is because we don't want to be overwriting the server
                                        // if it has a default of ON since that's something people would specifically
                                        // set and having a client change it could be dangerous (the reverse is much
                                        // less risky)

                                        // The full fix would probably be to make the options tri-state instead of 
                                        // just on/off, where the default is to use the servers default. Until that
                                        // happens though this is the best solution we came up with. See TFS#7937925

                                        // Note that users can always specifically add SET XACT_ABORT OFF to their 
                                        // queries if they do truly want to set it off. We just don't want  to
                                        // do it silently (since the default is going to be off)
                                        settings.XactAbortOn ? helper.SetXactAbortString : string.Empty);

                // append Ansi options
                sqlBuilder.AppendFormat(" {0} {1} {2} {3} {4} {5} {6}",
                                        helper.SetAnsiNullsString, helper.SetAnsiNullDefaultString, helper.SetAnsiPaddingString,
                                        helper.SetAnsiWarningsString, helper.SetCursorCloseOnCommitString,
                                        helper.SetImplicitTransactionString, helper.SetQuotedIdentifierString);
            }

            DbConnection dbConnection = await ConnectionService.GetOrOpenConnection(connection.OwnerUri, ConnectionType.Default);
            ReliableSqlConnection reliableSqlConnection = dbConnection as ReliableSqlConnection;
            if (reliableSqlConnection != null)
            {
                using (SqlCommand cmd = new SqlCommand(sqlBuilder.ToString(), reliableSqlConnection.GetUnderlyingConnection())) 
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.ExecuteNonQuery(); 
                }
            }
        }

        #endregion

        #region IDisposable Implementation

        private bool disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                foreach (var query in ActiveQueries)
                {
                    if (!query.Value.HasExecuted)
                    {
                        try
                        {
                            query.Value.Cancel();
                        }
                        catch (Exception e)
                        {
                            // We don't particularly care if we fail to cancel during shutdown
                            string message = string.Format("Failed to cancel query {0} during query service disposal: {1}", query.Key, e);
                            Logger.Warning(message);
                        }
                    }
                    query.Value.Dispose();
                }
                ActiveQueries.Clear();
            }

            disposed = true;
        }

        /// <summary>
        /// Verify if the URI maps to a query editor document
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        private bool isQueryEditor(string uri)
        {
            return (!string.IsNullOrWhiteSpace(uri)
                && (uri.StartsWith("untitled:")
                || uri.StartsWith("file:")));
        }

        ~QueryExecutionService()
        {
            Dispose(false);
        }

        #endregion
    }
}
