using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Common;
using Microsoft.TeamFoundation.Framework.Client;
using Microsoft.TeamFoundation.Framework.Common;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using MigrationTools._EngineV1.DataContracts;
using MigrationTools.Clients;
using MigrationTools.DataContracts;
using MigrationTools.Enrichers;
using MigrationTools.Processors.Infrastructure;
using MigrationTools.Services;
using MigrationTools.Tools;
using Serilog.Context;
using Serilog.Events;
using ILogger = Serilog.ILogger;

namespace MigrationTools.Processors
{
    /// <summary>
    /// WorkItemMigrationConfig is the main processor used to Migrate Work Items, Links, and Attachments.
    /// Use `WorkItemMigrationConfig` to configure.
    /// </summary>
    /// <status>ready</status>
    /// <processingtarget>Work Items</processingtarget>
    public class TfsWorkItemMigrationProcessor : TfsProcessor
    {
        private class ProgressTimer
        {
            private TimeSpan _totalProcessedTime = TimeSpan.Zero;

            public ProgressTimer(int totalCount)
            {
                TotalCount = totalCount;
            }

            public int TotalCount { get; private set; }
            public int ProcessedCount { get; private set; }
            public TimeSpan TotalProcessedTime => _totalProcessedTime;

            public void AddProcessedItem(TimeSpan processDuration, bool addToTotal)
            {
                if (addToTotal)
                {
                    TotalCount++;
                }
                ProcessedCount++;
                _totalProcessedTime += processDuration;
            }

            public TimeSpan AverageTime => ProcessedCount > 0
                ? TimeSpan.FromSeconds(TotalProcessedTime.TotalSeconds / ProcessedCount)
                : TotalProcessedTime;

            public TimeSpan RemainingTime => TimeSpan.FromTicks(AverageTime.Ticks * (TotalCount - ProcessedCount));
        }

        private static int _count = 0;
        private static int _current = 0;
        private static int _totalWorkItem = 0;
        private static string workItemLogTemplate = "[{sourceWorkItemTypeName,20}][Complete:{currentWorkItem,6}/{totalWorkItems}][sid:{sourceWorkItemId,6}|Rev:{sourceRevisionInt,3}][tid:{targetWorkItemId,6} | ";
        private List<string> _ignore;
        private Lazy<Dictionary<string, string>> _targetIdentitiesCache;

        private ILogger contextLog;
        private ILogger workItemLog;
        private List<string> _itemsInError;

        // Loop detection: track API comment sync frequency per WI pair
        private static readonly Dictionary<string, (int count, DateTime firstSeen)> _apiSyncTracker =
            new Dictionary<string, (int, DateTime)>();
        private static readonly object _trackerLock = new object();

        public WorkItemMetrics workItemMetrics { get; private set; }

        public TfsWorkItemMigrationProcessor(
            IOptions<TfsWorkItemMigrationProcessorOptions> options,
            TfsCommonTools tfsCommonTools,
            ProcessorEnricherContainer processorEnrichers,
            IServiceProvider services,
            ITelemetryLogger telemetry,
            ILogger<TfsWorkItemMigrationProcessor> logger)
            : base(options, tfsCommonTools, processorEnrichers, services, telemetry, logger)
        {
            contextLog = Serilog.Log.ForContext<TfsWorkItemMigrationProcessor>();
            workItemMetrics = services.GetRequiredService<WorkItemMetrics>();
        }

        new TfsWorkItemMigrationProcessorOptions Options => (TfsWorkItemMigrationProcessorOptions)base.Options;

        new TfsTeamProjectEndpoint Source => (TfsTeamProjectEndpoint)base.Source;

        new TfsTeamProjectEndpoint Target => (TfsTeamProjectEndpoint)base.Target;

        internal void TraceWriteLine(LogEventLevel level, string message, Dictionary<string, object> properties = null)
        {
            if (properties != null)
            {
                foreach (var item in properties)
                {
                    workItemLog = workItemLog.ForContext(item.Key, item.Value);
                }
            }
            workItemLog.Write(level, workItemLogTemplate + message);
        }

        protected override void InternalExecute()
        {
            Log.LogDebug("WorkItemMigrationContext::InternalExecute ");
            if (Options == null)
            {
                throw new Exception("You must call Configure() first");
            }
            //////////////////////////////////////////////////
            ValidatePatTokenRequirement();
            //////////////////////////////////////////////////

            _targetIdentitiesCache = new Lazy<Dictionary<string, string>>(() =>
            {
                try
                {
                    // Extract org name from target collection URI for VSSPS calls
                    Uri collUri = Target.Options.Collection;
                    string orgName = null;
                    if (collUri.Host.Contains("dev.azure.com") && collUri.Segments.Length > 1)
                        orgName = collUri.Segments[1].Trim('/');
                    else if (collUri.Host.Contains("visualstudio.com"))
                        orgName = collUri.Host.Split('.')[0];
                    if (string.IsNullOrWhiteSpace(orgName))
                    {
                        Log.LogWarning("Cannot determine target org name from {Uri} — mention rewriting disabled.", collUri);
                        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    string pat = Target.Options.Authentication.AccessToken;
                    string vsspsBase = $"https://vssps.dev.azure.com/{orgName}";
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    using (var client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

                        // Step 1: Get all users via Graph API
                        string json = client.GetStringAsync($"{vsspsBase}/_apis/graph/users?api-version=7.1-preview.1").Result;
                        var data = Newtonsoft.Json.Linq.JObject.Parse(json);
                        var users = data["value"] ?? new Newtonsoft.Json.Linq.JArray();

                        foreach (var user in users)
                        {
                            string displayName = user["displayName"]?.ToString();
                            string descriptor = user["descriptor"]?.ToString();
                            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(descriptor))
                                continue;

                            // Step 2: Resolve descriptor → TeamFoundationId via storage keys
                            try
                            {
                                string skJson = client.GetStringAsync(
                                    $"{vsspsBase}/_apis/graph/storagekeys/{descriptor}?api-version=7.1-preview.1").Result;
                                var skData = Newtonsoft.Json.Linq.JObject.Parse(skJson);
                                string tfId = skData["value"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(tfId))
                                    dict[displayName] = tfId;
                            }
                            catch { /* skip users whose storage key can't be resolved */ }
                        }
                    }
                    Log.LogInformation("Loaded {Count} identities from target via REST API for mention rewriting.", dict.Count);
                    return dict;
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Unable to load identities from target via REST API for comment mention rewriting.");
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            });

            ValidateWorkItemTypes();

            CommonTools.NodeStructure.ProcessorExecutionBegin(this);
            if (CommonTools.TeamSettings.Enabled)
            {
                CommonTools.TeamSettings.ProcessorExecutionBegin(this);
            }
            else
            {
                Log.LogWarning("WorkItemMigrationContext::InternalExecute: teamSettingsEnricher is disabled!");
            }

            _itemsInError = new List<string>();

            try
            {
                PopulateIgnoreList();

                // Inform the user that he maybe has to be patient now
                contextLog.Information("Querying items to be migrated: {SourceQuery} ...", Options.WIQLQuery);
                var sourceWorkItems = Source.WorkItems.GetWorkItems(Options.WIQLQuery);
                contextLog.Information("Replay all revisions of {sourceWorkItemsCount} work items?",
                    sourceWorkItems.Count);

                //////////////////////////////////////////////////
                CommonTools.NodeStructure.ValidateAllNodesExistOrAreMapped(this, sourceWorkItems, Source.WorkItems.Project.Name, Target.WorkItems.Project.Name);
                ValidateAllUsersExistOrAreMapped(sourceWorkItems);
                //////////////////////////////////////////////////

                contextLog.Information("Found target project as {@destProject}", Target.WorkItems.Project.Name);

                //////////////////////////////////////////////////////////FilterCompletedByQuery

                if (Options.FilterWorkItemsThatAlreadyExistInTarget)
                {
                    contextLog.Information(
                        "[FilterWorkItemsThatAlreadyExistInTarget] is enabled. Searching for {sourceWorkItems} work items that may have already been migrated to the target...",
                        sourceWorkItems.Count());

                    string targetWIQLQuery = CommonTools.NodeStructure.FixAreaPathAndIterationPathForTargetQuery(Options.WIQLQuery,
                        Source.WorkItems.Project.Name, Target.WorkItems.Project.Name, contextLog);
                    // Also replace Project Name
                    targetWIQLQuery = targetWIQLQuery.Replace(Source.WorkItems.Project.Name, Target.WorkItems.Project.Name);
                    //Then run query
                    sourceWorkItems = ((TfsWorkItemMigrationClient)Target.WorkItems).FilterExistingWorkItems(
                        sourceWorkItems, targetWIQLQuery,
                        (TfsWorkItemMigrationClient)Source.WorkItems);
                    contextLog.Information(
                        "!! After removing all found work items there are {SourceWorkItemCount} remaining to be migrated.",
                        sourceWorkItems.Count());
                }

                //////////////////////////////////////////////////


                _current = 1;
                _count = sourceWorkItems.Count;
                _totalWorkItem = sourceWorkItems.Count;
                ProgressTimer progressTimer = new ProgressTimer(_totalWorkItem);

                ProcessorActivity.SetTag("source_workitems_to_process", sourceWorkItems.Count);
                foreach (WorkItemData sourceWorkItemData in sourceWorkItems)
                {

                    var stopwatch = Stopwatch.StartNew();
                    var sourceWorkItem = TfsExtensions.ToWorkItem(sourceWorkItemData);
                    workItemLog = contextLog.ForContext("SourceWorkItemId", sourceWorkItem.Id);
                    using (LogContext.PushProperty("sourceWorkItemTypeName", sourceWorkItem.Type.Name))
                    using (LogContext.PushProperty("currentWorkItem", _current))
                    using (LogContext.PushProperty("totalWorkItems", _totalWorkItem))
                    using (LogContext.PushProperty("sourceWorkItemId", sourceWorkItem.Id))
                    using (LogContext.PushProperty("sourceRevisionInt", sourceWorkItem.Revision))
                    using (LogContext.PushProperty("targetWorkItemId", null))
                    {
                        try
                        {
                            ProcessWorkItemAsync(sourceWorkItemData, progressTimer, Options.WorkItemCreateRetryLimit).Wait();

                            stopwatch.Stop();
                            var processingTime = stopwatch.Elapsed.TotalMilliseconds;
                            workItemMetrics.WorkItemsProcessedCount.Add(1, new KeyValuePair<string, object?>("workItemType", sourceWorkItemData.Type));
                            workItemMetrics.ProcessingDuration.Record(processingTime, new KeyValuePair<string, object?>("workItemType", sourceWorkItemData.Type));
                            if (Options.PauseAfterEachWorkItem)
                            {
                                Console.WriteLine("Do you want to continue? (y/n)");
                                if (Console.ReadKey().Key != ConsoleKey.Y)
                                {
                                    workItemLog.Warning("USER ABORTED");
                                    break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            _itemsInError.Add(sourceWorkItem.Id.ToString());
                            workItemLog.Error(e, "Could not save migrated work item {WorkItemId}, an exception occurred.", sourceWorkItem.Id);

                            if (Options.MaxGracefulFailures == 0)
                            {
                                throw;
                            }

                            if (_itemsInError.Count > Options.MaxGracefulFailures)
                            {
                                throw new Exception($"Too many errors: more than {Options.MaxGracefulFailures} errors occurred, aborting migration.");
                            }
                        }
                    }
                }
            }
            finally
            {
                if (Options.FixHtmlAttachmentLinks)
                {
                    CommonTools.EmbededImages?.ProcessorExecutionEnd(null);
                }


                if (_itemsInError.Count > 0)
                {
                    contextLog.Warning("The following items could not be migrated: {ItemIds}", string.Join(", ", _itemsInError));
                }

                CommonTools.WorkItemLink.LogFailedLinksReport();
            }
        }

        private void ValidateWorkItemTypes()
        {
            Log.LogInformation("[ValidateWorkItemTypes]");
            Log.LogInformation("------------------------------------");
            Log.LogInformation("Starting Pre-Validation: Validating work item types and fields with `WorkItemTypeValidatorTool`.");
            Log.LogInformation("Refer to https://devopsmigration.io/TfsWorkItemTypeValidatorTool/ for configuration.");
            Log.LogInformation("------------------------------------");

            var sourceWits = Source.WorkItems.Project
                .ToProject()
                .WorkItemTypes
                .Cast<WorkItemType>()
                .OrderBy(wit => wit.Name)
                .ToList();
            var targetWits = Target.WorkItems.Project
                .ToProject()
                .WorkItemTypes
                .Cast<WorkItemType>()
                .OrderBy(wit => wit.Name)
                .ToList();

            // Reflected work item ID field is mandatory for migration, so it is validated even if the validator tool is disabled.
            bool containsReflectedWorkItemId = CommonTools.WorkItemTypeValidatorTool.ValidateReflectedWorkItemIdField(
                sourceWits, targetWits, Target.Options.ReflectedWorkItemIdField);
            bool validationResult = true;
            if (CommonTools.WorkItemTypeValidatorTool.Enabled)
            {
                validationResult = CommonTools.WorkItemTypeValidatorTool.ValidateWorkItemTypes(sourceWits, targetWits);
            }
            else
            {
                const string msg = $"Validation of work item types ({nameof(TfsWorkItemTypeValidatorTool)}) is disabled."
                    + " We suggest to enable this tool, so work item types in target system are properly validated."
                    + " If they are not validated, some data in source system may not be migrated,"
                    + " or migration may not work at all.";
                Log.LogWarning(msg);
            }
            if (!containsReflectedWorkItemId || !validationResult)
            {
                Log.LogError("Validation of work item types failed.");
                Environment.Exit(-1);
            }
            Log.LogInformation("------------------------------------");
            Log.LogInformation("[/ValidateWorkItemTypes]");
        }

        private void ValidateAllUsersExistOrAreMapped(List<WorkItemData> sourceWorkItems)
        {
            if (CommonTools.UserMapping.Options.SkipValidateAllUsersExistOrAreMapped)
            {
                contextLog.Information("Skipped: Validating::Check that all users in the source exist in the target or are mapped!");
                return;
            }

            contextLog.Information("Validating::Check that all users in the source exist in the target or are mapped!");
            IdentityMapResult usersToMap = CommonTools.UserMapping.GetUsersInSourceMappedToTargetForWorkItems(this, sourceWorkItems);
            if (usersToMap != null && usersToMap.IdentityMap != null && usersToMap.IdentityMap.Count > 0)
            {
                Log.LogWarning("Validating Failed! There are {usersToMap} users that exist in the source that do not exist "
                    + "in the target. This will not cause any errors, but may result in disconnected users that could have "
                    + "been mapped. Use the ExportUsersForMapping processor to create a list of mappable users.",
                    usersToMap.IdentityMap.Count);
            }
        }

        //private void ValidateAllNodesExistOrAreMapped(List<WorkItemData> sourceWorkItems)
        //{
        //    contextLog.Information("Validating::Check that all Area & Iteration paths from Source have a valid mapping on Target");
        //    if (!TfsCommonTools.NodeStructure.Options.Enabled && Target.Options.Project != Source.Config.AsTeamProjectConfig().Project)
        //    {
        //        Log.LogError("Source and Target projects have different names, but  NodeStructureEnricher is not enabled. Cant continue... please enable nodeStructureEnricher in the config and restart.");
        //        Environment.Exit(-1);
        //    }
        //    if ( TfsCommonTools.NodeStructure.Options.Enabled)
        //    {
        //        List<NodeStructureItem> nodeStructureMissingItems = TfsCommonTools.NodeStructure.GetMissingRevisionNodes(sourceWorkItems);
        //        if (TfsCommonTools.NodeStructure.ValidateTargetNodesExist(nodeStructureMissingItems))
        //        {
        //            Log.LogError("Missing Iterations in Target preventing progress, check log for list. To continue you MUST configure IterationMaps or AreaMaps that matches the missing paths..");
        //            Environment.Exit(-1);
        //        }
        //    } else
        //    {
        //        contextLog.Error("nodeStructureEnricher is disabled! Please enable it in the config.");
        //    }
        //}

        private void ValidatePatTokenRequirement()
        {
            string collUrl = Target.Options.Collection.ToString();
            if (collUrl.Contains("dev.azure.com") || collUrl.Contains(".visualstudio.com"))
            {
                var token = Target.Options.Authentication.AccessToken;
                // Test that
                if (token.IsNullOrEmpty())
                {
                    var ex = new InvalidOperationException("Missing PersonalAccessToken from Target");
                    Log.LogError(ex, "When you are migrating to Azure DevOps you MUST provide an PAT so that we can call the REST API for certain actions. For example we would be unable to deal with a Work item Type change.");
                    Environment.Exit(-1);
                }
            }
        }

        private static bool IsNumeric(string val, NumberStyles numberStyle)
        {
            double result;
            return double.TryParse(val, numberStyle,
                CultureInfo.CurrentCulture, out result);
        }

        private WorkItemData CreateWorkItem_Shell(ProjectData destProject, WorkItemData currentRevisionWorkItem, string destType)
        {
            using (var activity = ActivitySourceProvider.ActivitySource.StartActivity("CreateWorkItem_Shell", ActivityKind.Client))
            {
                activity?.SetTagsFromOptions(Options);
                activity?.SetTag("http.request.method", "GET");
                activity?.SetTag("migrationtools.client", "TfsObjectModel");

                WorkItem newwit;
                if (destProject.ToProject().WorkItemTypes.Contains(destType))
                {
                    newwit = destProject.ToProject().WorkItemTypes[destType].NewWorkItem();
                }
                else
                {
                    throw new Exception($"WARNING: Unable to find '{destType}' in the target project. Either the work item specific is from the source, or its being specified in the {nameof(WorkItemTypeMappingTool)} definition in your configuration file! ");
                }
                activity?.Stop();
                activity?.SetStatus(ActivityStatusCode.Ok);
                activity?.SetTag("http.response.status_code", "200");
                if (Target.Options.ProductVersion != Endpoints.TfsProductVersion.OnPremisesClassic)
                {
                    newwit.Fields["System.CreatedBy"].Value = currentRevisionWorkItem.ToWorkItem().Revisions[0].Fields["System.CreatedBy"].Value;
                    workItemLog.Debug("Setting 'System.CreatedBy'={SystemCreatedBy}", currentRevisionWorkItem.ToWorkItem().Revisions[0].Fields["System.CreatedBy"].Value);
                    newwit.Fields["System.CreatedDate"].Value = currentRevisionWorkItem.ToWorkItem().Revisions[0].Fields["System.CreatedDate"].Value;
                    workItemLog.Debug("Setting 'System.CreatedDate'={SystemCreatedDate}", currentRevisionWorkItem.ToWorkItem().Revisions[0].Fields["System.CreatedDate"].Value);
                }
                return newwit.AsWorkItemData();
            }
        }

        private void PopulateIgnoreList()
        {
            _ignore = new List<string>
            {
                "System.Rev",
                "System.AreaId",
                "System.IterationId",
                "System.Id",
                "System.Parent",
                "System.RevisedDate",
                "System.AuthorizedAs",
                "System.AttachedFileCount",
                "System.TeamProject",
                "System.NodeName",
                "System.RelatedLinkCount",
                "System.WorkItemType",
                "Microsoft.VSTS.Common.StateChangeDate",
                "System.ExternalLinkCount",
                "System.HyperLinkCount",
                "System.Watermark",
                "System.AuthorizedDate",
                "System.BoardColumn",
                "System.BoardColumnDone",
                "System.BoardLane",
                "SLB.SWT.DateOfClientFeedback",
                "System.CommentCount",
                "System.RemoteLinkCount"
            };
        }

        // TODO : Make this into the Work Item mapping tool
        private void PopulateWorkItem(WorkItemData oldWorkItemData, WorkItemData newWorkItemData, string destType, bool applyHistoryViaObjectModel = true, ICollection<string> fieldFilter = null)
        {
            var oldWorkItem = oldWorkItemData.ToWorkItem();
            var newWorkItem = newWorkItemData.ToWorkItem();
            var fieldMappingTimer = Stopwatch.StartNew();

            if (newWorkItem.IsPartialOpen || !newWorkItem.IsOpen)
            {
                newWorkItem.Open();
            }

            if (ShouldApplyDeltaField("System.Title", fieldFilter))
            {
                newWorkItem.Title = oldWorkItem.Title;
            }
            if (ShouldApplyDeltaField("System.State", fieldFilter))
            {
                newWorkItem.State = oldWorkItem.State;
            }
            try
            {
                if (ShouldApplyDeltaField("Microsoft.VSTS.Common.ClosedDate", fieldFilter) &&
                    newWorkItem.Fields.Contains("Microsoft.VSTS.Common.ClosedDate") && newWorkItem.Fields["Microsoft.VSTS.Common.ClosedDate"].IsEditable)
                {
                    newWorkItem.Fields["Microsoft.VSTS.Common.ClosedDate"].Value = oldWorkItem.Fields["Microsoft.VSTS.Common.ClosedDate"].Value;
                }
            }
            catch (FieldDefinitionNotExistException)
            {
                // Intentionally swallowed - field may not exist in target project
            }
            if (ShouldApplyDeltaField("System.Reason", fieldFilter))
            {
                newWorkItem.Reason = oldWorkItem.Reason;
            }

            foreach (Field f in oldWorkItem.Fields)
            {
                if (!ShouldApplyDeltaField(f.ReferenceName, fieldFilter))
                {
                    continue;
                }

                if (!applyHistoryViaObjectModel && f.ReferenceName == "System.History")
                {
                    continue;
                }

                CommonTools.UserMapping.MapUserIdentityField(f);
                if (newWorkItem.Fields.Contains(f.ReferenceName))
                {
                    CommonTools.UserMapping.MapUserIdentityField(newWorkItem.Fields[f.ReferenceName]);
                }

                if (newWorkItem.Fields.Contains(f.ReferenceName) == false)
                {
                    var missedMigratedValue = oldWorkItem.Fields[f.ReferenceName].Value;
                    if (missedMigratedValue != null && !string.Empty.Equals(missedMigratedValue))
                    {
                        Log.LogWarning("PopulateWorkItem:FieldUpdate: Missing field in target workitem, Source WorkItemId: {WorkitemId}, Field: {MissingField}, Value: {SourceValue}", oldWorkItemData.Id, f.ReferenceName, missedMigratedValue);
                    }
                    continue;
                }
                if (!_ignore.Contains(f.ReferenceName) &&
                    (!newWorkItem.Fields[f.ReferenceName].IsChangedInRevision || newWorkItem.Fields[f.ReferenceName].IsEditable)
                    && oldWorkItem.Fields[f.ReferenceName].Value != newWorkItem.Fields[f.ReferenceName].Value)
                {
                    Log.LogDebug("PopulateWorkItem:FieldUpdate: {ReferenceName} | Source:{OldReferenceValue} Target:{NewReferenceValue}", f.ReferenceName, oldWorkItem.Fields[f.ReferenceName].Value, newWorkItem.Fields[f.ReferenceName].Value);

                    switch (f.FieldDefinition.FieldType)
                    {
                        case FieldType.String:
                            string oldValue = oldWorkItem.Fields[f.ReferenceName].Value.ToString();
                            string newValue = CommonTools.StringManipulator.ProcessString(oldValue);
                            newWorkItem.Fields[f.ReferenceName].Value = f.ReferenceName == "System.Tags" && fieldFilter != null
                                ? MergeTags(newWorkItem.Fields[f.ReferenceName].Value?.ToString(), newValue)
                                : newValue;
                            break;
                        default:
                            newWorkItem.Fields[f.ReferenceName].Value = oldWorkItem.Fields[f.ReferenceName].Value;
                            break;
                    }

                }
            }

            if (CommonTools.NodeStructure.Enabled)
            {

                if (ShouldApplyDeltaField("System.AreaPath", fieldFilter))
                {
                    newWorkItem.AreaPath = CommonTools.NodeStructure.GetNewNodeName(oldWorkItem.AreaPath, TfsNodeStructureType.Area);
                }
                if (ShouldApplyDeltaField("System.IterationPath", fieldFilter))
                {
                    newWorkItem.IterationPath = CommonTools.NodeStructure.GetNewNodeName(oldWorkItem.IterationPath, TfsNodeStructureType.Iteration);
                }
            }
            else
            {
                Log.LogWarning("WorkItemMigrationContext::PopulateWorkItem::nodeStructureEnricher::Disabled! This needs to be set to true!");
            }

            switch (destType)
            {
                case "Test Case":
                    try
                    {
                        if (ShouldApplyDeltaField("Microsoft.VSTS.TCM.Steps", fieldFilter))
                        {
                            newWorkItem.Fields["Microsoft.VSTS.TCM.Steps"].Value = oldWorkItem.Fields["Microsoft.VSTS.TCM.Steps"].Value;
                        }
                    }
                    catch (FieldDefinitionNotExistException ex)
                    {
                        Log.LogWarning($"Microsoft.VSTS.TCM.Steps does not exist on Source Work Item. This field will be skipped, but the all other fields on the revision will be populated. Exception details: {ex.Message}");
                    }
                    if (ShouldApplyDeltaField("Microsoft.VSTS.Common.Priority", fieldFilter))
                    {
                        newWorkItem.Fields["Microsoft.VSTS.Common.Priority"].Value =
                            oldWorkItem.Fields["Microsoft.VSTS.Common.Priority"].Value;
                    }
                    break;
            }

            if (ShouldApplyDeltaField("Microsoft.VSTS.Common.BacklogPriority", fieldFilter)
                && newWorkItem.Fields.Contains("Microsoft.VSTS.Common.BacklogPriority")
                && newWorkItem.Fields["Microsoft.VSTS.Common.BacklogPriority"].Value != null
                && !IsNumeric(newWorkItem.Fields["Microsoft.VSTS.Common.BacklogPriority"].Value.ToString(),
                    NumberStyles.Any))
                newWorkItem.Fields["Microsoft.VSTS.Common.BacklogPriority"].Value = 10;

            if (ShouldApplyDeltaField("System.Description", fieldFilter))
            {
                var description = new StringBuilder();
                description.Append(oldWorkItem.Description);
                newWorkItem.Description = description.ToString();
            }
            fieldMappingTimer.Stop();
        }

        private static bool ShouldApplyDeltaField(string referenceName, ICollection<string> fieldFilter)
        {
            return fieldFilter == null || fieldFilter.Contains(referenceName);
        }

        private static string MergeTags(string currentTags, string incomingTags)
        {
            var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string tagList in new[] { currentTags, incomingTags })
            {
                if (string.IsNullOrWhiteSpace(tagList))
                {
                    continue;
                }
                foreach (string tag in tagList.Split(';'))
                {
                    string normalized = tag.Trim();
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        tags.Add(normalized);
                    }
                }
            }
            return string.Join("; ", tags);
        }

        private void ProcessHTMLFieldAttachements(WorkItemData targetWorkItem)
        {
            if (targetWorkItem != null && Options.FixHtmlAttachmentLinks)
            {
                CommonTools.EmbededImages.FixEmbededImages(this, targetWorkItem);
            }
        }

        private void ProcessWorkItemEmbeddedLinks(WorkItemData sourceWorkItem, WorkItemData targetWorkItem)
        {
            if (sourceWorkItem != null && targetWorkItem != null && Options.FixHtmlAttachmentLinks)
            {
                CommonTools.WorkItemEmbededLink.Enrich(this, sourceWorkItem, targetWorkItem);
            }
        }

        private async Task ProcessWorkItemAsync(WorkItemData sourceWorkItem, ProgressTimer progressTimer, int retryLimit = 5, int retries = 0)
        {
            using (var activity = ActivitySourceProvider.ActivitySource.StartActivity("ProcessWorkItemAsync", ActivityKind.Client))
            {
                activity?.SetTagsFromOptions(Options);
                activity?.SetTag("http.request.method", "GET");
                activity?.SetTag("migrationtools.client", "TfsObjectModel");
                activity?.SetTag("SourceURL", Source.Options.Collection.ToString());
                activity?.SetTag("SourceWorkItem", sourceWorkItem.Id);
                activity?.SetTag("TargetURL", Target.Options.Collection.ToString());
                activity?.SetTag("TargetProject", Target.WorkItems.Project.Name);
                activity?.SetTag("RetryLimit", retryLimit.ToString());
                activity?.SetTag("RetryNumber", retries.ToString());
                Log.LogDebug("######################################################################################");
                Log.LogDebug("ProcessWorkItem: {sourceWorkItemId}", sourceWorkItem.Id);
                Log.LogDebug("######################################################################################");
                try
                {
                    if (sourceWorkItem.Type != "Test Plan" && sourceWorkItem.Type != "Test Suite")
                    {
                        workItemMetrics.RevisionsPerWorkItem.Record(sourceWorkItem.Rev);
                        var targetWorkItem = Target.WorkItems.FindReflectedWorkItem(sourceWorkItem, false);
                        ///////////////////////////////////////////////
                        TraceWriteLine(LogEventLevel.Information, "Work Item has {sourceWorkItemRev} revisions and revision migration is set to {ReplayRevisions}",
                            new Dictionary<string, object>(){
                            { "sourceWorkItemRev", sourceWorkItem.Rev },
                            { "ReplayRevisions", CommonTools.RevisionManager.ReplayRevisions }}
                            );
                        EventDeltaOptions eventDelta = GetEventDeltaOptionsFromEnvironment();
                        if (targetWorkItem == null)
                        {
                            List<RevisionItem> revisionsToMigrate = CommonTools.RevisionManager.GetRevisionsToMigrate(sourceWorkItem.Revisions.Values.ToList(), null);
                            targetWorkItem = ReplayRevisions(revisionsToMigrate, sourceWorkItem, null);
                            activity?.SetTag("Revisions", revisionsToMigrate.Count);
                        }
                        else if (eventDelta.HasPayload)
                        {
                            if (eventDelta.HasObjectModelFields)
                            {
                                TraceWriteLine(LogEventLevel.Information, "Applying event delta for revision {RevisionNumber} with {FieldCount} fields",
                                    new Dictionary<string, object>()
                                    {
                                        { "RevisionNumber", eventDelta.RevisionNumber.GetValueOrDefault() },
                                        { "FieldCount", eventDelta.FieldNames.Count }
                                    });
                                targetWorkItem = ApplyEventDelta(eventDelta, sourceWorkItem, targetWorkItem);
                                activity?.SetTag("Revisions", 1);
                            }
                            else
                            {
                                TraceWriteLine(LogEventLevel.Information, "Skipping Object Model replay for event revision {RevisionNumber}; no delta-owned fields",
                                    new Dictionary<string, object>() { { "RevisionNumber", eventDelta.RevisionNumber.GetValueOrDefault() } });
                                activity?.SetTag("Revisions", 0);
                            }
                        }
                        else
                        {
                            List<RevisionItem> revisionsToMigrate = CommonTools.RevisionManager.GetRevisionsToMigrate(sourceWorkItem.Revisions.Values.ToList(), targetWorkItem?.Revisions.Values.ToList());
                            if (revisionsToMigrate.Count == 0)
                            {
                                ProcessWorkItemAttachments(sourceWorkItem, targetWorkItem, false);
                                ProcessWorkItemLinks(sourceWorkItem, targetWorkItem);
                                ProcessHTMLFieldAttachements(targetWorkItem);
                                ProcessWorkItemEmbeddedLinks(sourceWorkItem, targetWorkItem);
                                CommonTools.FieldMappingTool.ApplyFieldMappings(sourceWorkItem, targetWorkItem);
                                TraceWriteLine(LogEventLevel.Information, "Skipping as work item exists and no revisions to sync detected");
                                activity?.SetTag("Revisions", 0);
                            }
                            else
                            {
                                TraceWriteLine(LogEventLevel.Information, "Syncing as there are {revisionsToMigrateCount} revisions detected",
                                    new Dictionary<string, object>(){
                                    { "revisionsToMigrateCount", revisionsToMigrate.Count }
                                    });

                                targetWorkItem = ReplayRevisions(revisionsToMigrate, sourceWorkItem, targetWorkItem);
                            }
                        }
                        if (targetWorkItem != null && targetWorkItem.ToWorkItem().IsDirty)
                        {
                            targetWorkItem.SaveToAzureDevOps();
                        }
                        else if (targetWorkItem != null)
                        {
                            TraceWriteLine(LogEventLevel.Information, "Skipped save for {TargetWorkItemId}, no changes detected",
                                new Dictionary<string, object>() { { "TargetWorkItemId", targetWorkItem.Id } });
                        }
                        if (targetWorkItem != null)
                        {
                            // Always run comment sync: the API owns post-cutoff comments and
                            // can still inject markers into legacy content-matched comments.
                            await SyncMissingCommentsAsync(sourceWorkItem, targetWorkItem);
                            targetWorkItem.ToWorkItem().Close();
                        }
                        if (sourceWorkItem != null)
                        {
                            sourceWorkItem.ToWorkItem().Close();
                        }
                    }
                    else
                    {
                        TraceWriteLine(LogEventLevel.Warning, "SKIP: Unable to migrate {sourceWorkItemTypeName}/{sourceWorkItemId}. Use the TestPlansAndSuitesMigrationContext after you have migrated all Test Cases. ",
                            new Dictionary<string, object>() {
                            {"sourceWorkItemTypeName", sourceWorkItem.Type },
                            {"sourceWorkItemId", sourceWorkItem.Id }
                            });
                    }
                }
                catch (WebException ex)
                {
                    Log.LogError(ex, "Some kind of internet pipe blockage");
                    if (retries < retryLimit)
                    {
                        TraceWriteLine(LogEventLevel.Warning, "WebException: Will retry in {retrys}s ",
                            new Dictionary<string, object>() {
                            {"retrys", retries }
                            });
                        System.Threading.Thread.Sleep(new TimeSpan(0, 0, retries));
                        retries++;
                        TraceWriteLine(LogEventLevel.Warning, "RETRY {Retrys}/{RetryLimit} ",
                            new Dictionary<string, object>() {
                            {"Retrys", retries },
                            {"RetryLimit", retryLimit }
                            });
                        await ProcessWorkItemAsync(sourceWorkItem, progressTimer, retryLimit, retries);
                    }
                    else
                    {
                        TraceWriteLine(LogEventLevel.Error, "ERROR: Failed to create work item. Retry Limit reached ");
                    }
                }
                catch (Exception ex)
                {
                    activity?.Stop();
                    activity?.SetStatus(ActivityStatusCode.Error);
                    activity?.SetTag("http.response.status_code", "502");
                    Log.LogError(ex, ex.ToString());
                    Telemetry.TrackException(ex, activity?.Tags);
                    throw;
                }
                activity?.Stop();
                progressTimer.AddProcessedItem(activity.Duration, retries > 0);
                TraceWriteLine(LogEventLevel.Information,
                    "Average time of {average:%s}.{average:%fff} per work item and {remaining:%h} hours {remaining:%m} minutes {remaining:%s} seconds estimated to completion.",
                    new Dictionary<string, object>() {
                        { "average", progressTimer.AverageTime },
                        { "remaining", progressTimer.RemainingTime }
                    });

                activity?.SetStatus(ActivityStatusCode.Error);
                activity?.SetTag("http.response.status_code", "200");

                _current++;
                _count--;
            }
        }

        private void ProcessWorkItemAttachments(WorkItemData sourceWorkItem, WorkItemData targetWorkItem, bool save = true)
        {
            if (targetWorkItem != null && CommonTools.Attachment.Enabled && sourceWorkItem.ToWorkItem().Attachments.Count > 0)
            {
                TraceWriteLine(LogEventLevel.Information, "Attachemnts {SourceWorkItemAttachmentCount} | LinkMigrator:{AttachmentMigration}", new Dictionary<string, object>() { { "SourceWorkItemAttachmentCount", sourceWorkItem.ToWorkItem().Attachments.Count }, { "AttachmentMigration", CommonTools.Attachment.Enabled } });
                // Use SmartProcessAttachments for intelligent duplicate handling with MD5 checksums
                CommonTools.Attachment.SmartProcessAttachments(this, sourceWorkItem, targetWorkItem, save);
                //AddMetric("Attachments", processWorkItemMetrics, targetWorkItem.ToWorkItem().AttachedFileCount);
            }
        }

        private void ProcessWorkItemLinks(WorkItemData sourceWorkItem, WorkItemData targetWorkItem)
        {
            if (targetWorkItem != null && CommonTools.WorkItemLink.Enabled && sourceWorkItem.ToWorkItem().Links.Count > 0)
            {
                TraceWriteLine(LogEventLevel.Information, "Links {SourceWorkItemLinkCount} | LinkMigrator:{LinkMigration}", new Dictionary<string, object>() { { "SourceWorkItemLinkCount", sourceWorkItem.ToWorkItem().Links.Count }, { "LinkMigration", CommonTools.WorkItemLink.Enabled } });
                CommonTools.WorkItemLink.Enrich(this, sourceWorkItem, targetWorkItem);
                //AddMetric("RelatedLinkCount", processWorkItemMetrics, targetWorkItem.ToWorkItem().Links.Count);
                int fixedLinkCount = CommonTools.GitRepository.Enrich(this, sourceWorkItem, targetWorkItem);
                // AddMetric("FixedGitLinkCount", processWorkItemMetrics, fixedLinkCount);
            }
            else if (targetWorkItem != null && sourceWorkItem.ToWorkItem().Links.Count > 0 && sourceWorkItem.Type == "Test Case")
            {
                CommonTools.WorkItemLink.MigrateSharedSteps(this, sourceWorkItem, targetWorkItem);
                CommonTools.WorkItemLink.MigrateSharedParameters(this, sourceWorkItem, targetWorkItem);
            }
        }

        private readonly Dictionary<int, int> _sourceToTargetIdCache = new Dictionary<int, int>();

        private int? ResolveTargetWorkItemId(int sourceWorkItemId)
        {
            if (_sourceToTargetIdCache.TryGetValue(sourceWorkItemId, out int cached))
                return cached == -1 ? (int?)null : cached;

            try
            {
                string sourceOrg = Source.Options.Collection.AbsoluteUri.TrimEnd('/');
                string sourceProject = Source.Options.Project;
                string reflectedId = $"{sourceOrg}/{sourceProject}/_workitems/edit/{sourceWorkItemId}";
                var targetWi = Target.WorkItems.FindReflectedWorkItemByReflectedWorkItemId(reflectedId);
                if (targetWi != null && int.TryParse(targetWi.Id, out int targetId))
                {
                    _sourceToTargetIdCache[sourceWorkItemId] = targetId;
                    return targetId;
                }
            }
            catch { }
            _sourceToTargetIdCache[sourceWorkItemId] = -1;
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex MentionAnchorRegex =
            new System.Text.RegularExpressions.Regex(
                @"<a[^>]*?href=""(?<href>[^""]*?)""[^>]*?(?<version>data-vss-mention=""[^""]*?"")[^>]*>(?<value>.*?)</a>",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

        private string RewriteCommentMentions(string commentHtml)
        {
            if (string.IsNullOrWhiteSpace(commentHtml) || _targetIdentitiesCache == null) return commentHtml;

            return MentionAnchorRegex.Replace(commentHtml, match =>
            {
                var href = match.Groups["href"].Value;
                var version = match.Groups["version"].Value;
                var value = match.Groups["value"].Value;

                if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(value))
                    return match.Value;

                // Only process user mentions (@DisplayName with href=# or mailto:)
                if (!(href.StartsWith("#") || href.StartsWith("mailto:")) || !value.StartsWith("@"))
                    return match.Value;

                var displayName = value.Substring(1);
                if (_targetIdentitiesCache.Value.TryGetValue(displayName, out string targetOriginId))
                {
                    return match.Value
                        .Replace(href, "#")
                        .Replace(version, $"data-vss-mention=\"version:2.0,{targetOriginId}\"");
                }

                return match.Value;
            });
        }

        private string RewriteCommentWorkItemLinks(string commentHtml)
        {
            if (string.IsNullOrWhiteSpace(commentHtml)) return commentHtml;

            string targetOrg = Target.Options.Collection.AbsoluteUri.TrimEnd('/');
            string targetProject = Target.Options.Project;

            // Match full <a> tags containing work item URLs, capturing the link text
            var wiAnchorRegex = new System.Text.RegularExpressions.Regex(
                @"(<a\b[^>]*href="")(https?://dev\.azure\.com/[^""]+/_workitems/edit/)(\d+)(""[^>]*>)(.*?)(</a>)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            commentHtml = wiAnchorRegex.Replace(commentHtml, match =>
            {
                if (int.TryParse(match.Groups[3].Value, out int sourceId))
                {
                    int? targetId = ResolveTargetWorkItemId(sourceId);
                    if (targetId.HasValue)
                    {
                        string newUrl = $"{targetOrg}/{Uri.EscapeDataString(targetProject)}/_workitems/edit/{targetId.Value}";
                        // Also rewrite #ID in the link text
                        string linkText = match.Groups[5].Value;
                        linkText = System.Text.RegularExpressions.Regex.Replace(
                            linkText, @"#\d+", $"#{targetId.Value}");
                        return match.Groups[1].Value + newUrl + match.Groups[4].Value + linkText + match.Groups[6].Value;
                    }
                }
                return match.Value;
            });

            // Match standalone work item URLs not inside <a> tags
            var wiUrlRegex = new System.Text.RegularExpressions.Regex(
                @"(?<!href="")https?://dev\.azure\.com/[^""'\s]+/_workitems/edit/(?<id>\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            commentHtml = wiUrlRegex.Replace(commentHtml, match =>
            {
                if (int.TryParse(match.Groups["id"].Value, out int sourceId))
                {
                    int? targetId = ResolveTargetWorkItemId(sourceId);
                    if (targetId.HasValue)
                        return $"{targetOrg}/{Uri.EscapeDataString(targetProject)}/_workitems/edit/{targetId.Value}";
                }
                return match.Value;
            });

            // Match #ID in plain text (not inside <a> tags) — minimum 1 digit
            var hashMentionRegex = new System.Text.RegularExpressions.Regex(
                @"(?<=>)([^<]*?)#(\d+)");
            commentHtml = hashMentionRegex.Replace(commentHtml, match =>
            {
                if (int.TryParse(match.Groups[2].Value, out int sourceId))
                {
                    int? targetId = ResolveTargetWorkItemId(sourceId);
                    if (targetId.HasValue)
                        return $"{match.Groups[1].Value}#{targetId.Value}";
                }
                return match.Value;
            });

            return commentHtml;
        }

        private Newtonsoft.Json.Linq.JArray GetCommentsViaApi(string baseUri, string project, string token, int workItemId)
        {
            string requestUri = $"{baseUri.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_apis/wit/workItems/{workItemId}/comments?api-version=7.1-preview.4&$top=200";
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));
                string json = client.GetStringAsync(requestUri).Result;
                var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json))
                    { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
                var data = Newtonsoft.Json.Linq.JObject.Load(reader);
                return (Newtonsoft.Json.Linq.JArray)data["comments"] ?? new Newtonsoft.Json.Linq.JArray();
            }
        }

        private DateTime? GetLatestCommentDate(string baseUri, string project, string token, int workItemId)
        {
            try
            {
                // Fetch recent comments (newest first) and find the latest NATIVE one
                // (skip synced comments whose createdDate reflects the API call time, not the original date)
                string requestUri = $"{baseUri.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_apis/wit/workItems/{workItemId}/comments?api-version=7.1-preview.4&$top=10";
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));
                    string json = client.GetStringAsync(requestUri).Result;
                    var reader = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(json))
                        { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
                    var data = Newtonsoft.Json.Linq.JObject.Load(reader);
                    var comments = data["comments"] as Newtonsoft.Json.Linq.JArray;
                    if (comments != null)
                    {
                        foreach (var c in comments)
                        {
                            string text = c["text"]?.ToString() ?? "";
                            // Skip synced comments (their createdDate is the sync time, not original)
                            if (SyncSrcMarkerRegex.IsMatch(text)) continue;
                            if (text.Contains("[Original date:")) continue;

                            string dateStr = c["createdDate"]?.ToString();
                            if (DateTime.TryParse(dateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                                return parsed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TraceWriteLine(LogEventLevel.Warning, "GetLatestCommentDate failed for WI {WorkItemId}: {Error}",
                    new Dictionary<string, object>() { { "WorkItemId", workItemId }, { "Error", ex.Message } });
            }
            return null;
        }

        private string GetSourceOrgName()
        {
            Uri uri = Source.Options.Collection;
            if (uri.Host.Contains("dev.azure.com") && uri.Segments.Length > 1)
                return uri.Segments[1].Trim('/');
            if (uri.Host.Contains("visualstudio.com"))
                return uri.Host.Split('.')[0];
            return uri.Host;
        }

        private static readonly System.Text.RegularExpressions.Regex SyncSrcMarkerRegex =
            new System.Text.RegularExpressions.Regex(
                @"<span\s+style=""display:none;?"">sync-src:([^:]+):(\d+)</span>",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Normalize comment HTML for cross-org content comparison.
        /// Strips/neutralizes all org-specific content (attachment URLs, WI refs, mention GUIDs, markers, date headers).</summary>
        private static string NormalizeCommentForComparison(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            string n = html;
            // Strip sync-src markers
            n = SyncSrcMarkerRegex.Replace(n, "");
            // Strip [Original date:] headers
            n = System.Text.RegularExpressions.Regex.Replace(n, @"<b>\[Original date:[^\]]*\]</b><br>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Normalize <img> tags (attachment URLs differ cross-org)
            n = System.Text.RegularExpressions.Regex.Replace(n, @"<img\s[^>]*>", "[IMG]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Normalize attachment URLs in <a href> and other contexts
            n = System.Text.RegularExpressions.Regex.Replace(n, @"https?://dev\.azure\.com/[^""'\s]+/_apis/wit/attachments/[^""'\s]+", "[ATTACH]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Normalize WI edit URLs: dev.azure.com/ORG/PROJECT/_workitems/edit/ID → [WI_URL]
            n = System.Text.RegularExpressions.Regex.Replace(n, @"https?://dev\.azure\.com/[^""'\s]+/_workitems/edit/\d+", "[WI_URL]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Normalize #ID WI references (4+ digits)
            n = System.Text.RegularExpressions.Regex.Replace(n, @"(?<=#)\d{4,}", "[ID]");
            // Normalize mention GUIDs
            n = System.Text.RegularExpressions.Regex.Replace(n, @"data-vss-mention=""[^""]*""", "data-vss-mention=\"\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Normalize any remaining org/project GUIDs in URLs
            n = System.Text.RegularExpressions.Regex.Replace(n, @"https?://dev\.azure\.com/[^/""'\s]+/[^/""'\s]+/", "[ADO_BASE]/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return n.Trim();
        }

        // Antiloop: service account emails read from ANTILOOP_ACCOUNTS env var (comma-separated).
        private static readonly HashSet<string> AntiloopAccounts = ParseAntiloopAccounts();

        private static HashSet<string> ParseAntiloopAccounts()
        {
            string env = Environment.GetEnvironmentVariable("ANTILOOP_ACCOUNTS");
            var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(env))
            {
                foreach (var account in env.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    accounts.Add(account.Trim());
                }
            }
            return accounts;
        }

        private static bool IsAntiloopAccount(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return false;
            return AntiloopAccounts.Any(a =>
                identity.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (a.Contains("@") && identity.IndexOf(a.Split('@')[0], StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static string GetRevisionFieldValue(RevisionItem revision, string fieldName)
        {
            return revision.Fields.ContainsKey(fieldName)
                ? revision.Fields[fieldName].Value?.ToString() ?? ""
                : "";
        }

        private static bool IsSyncGeneratedRevision(RevisionItem revision)
        {
            string revHistory = GetRevisionFieldValue(revision, "System.History");
            return
                IsAntiloopAccount(GetRevisionFieldValue(revision, "System.ChangedBy")) ||
                IsAntiloopAccount(GetRevisionFieldValue(revision, "System.AuthorizedAs")) ||
                revHistory.Contains("[Synced comment -") ||
                revHistory.Contains("[BACKFILL -") ||
                SyncSrcMarkerRegex.IsMatch(revHistory);
        }

        private static bool ShouldApplyHistoryViaObjectModel(RevisionItem revision, DateTime commentSyncCutoffDate)
        {
            if (revision == null) return false;

            string history = GetRevisionFieldValue(revision, "System.History");
            if (string.IsNullOrWhiteSpace(history)) return false;

            return revision.ChangedDate.ToUniversalTime() < commentSyncCutoffDate.ToUniversalTime();
        }

        private static bool IsObjectModelReplayField(string referenceName, ICollection<string> ignoredFields)
        {
            if (string.IsNullOrWhiteSpace(referenceName)) return false;
            if (referenceName == "System.History") return false;
            if (referenceName == "System.ChangedBy") return false;
            if (referenceName == "System.ChangedDate") return false;
            if (ignoredFields != null && ignoredFields.Any(f => string.Equals(f, referenceName, StringComparison.OrdinalIgnoreCase))) return false;

            return true;
        }

        private static bool RevisionHasObjectModelReplayChanges(RevisionItem revision, ICollection<string> ignoredFields)
        {
            if (revision?.Fields == null) return true;

            return revision.Fields.Keys.Any(referenceName => IsObjectModelReplayField(referenceName, ignoredFields));
        }

        private sealed class EventDeltaOptions
        {
            public int? RevisionNumber { get; set; }
            public string ChangedFieldsJson { get; set; }
            public JObject ChangedFields { get; set; }
            public ICollection<string> FieldNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool HasPayload => RevisionNumber.HasValue && !string.IsNullOrWhiteSpace(ChangedFieldsJson);
            public bool HasObjectModelFields => HasPayload && FieldNames.Count > 0;
        }

        private static EventDeltaOptions GetEventDeltaOptionsFromEnvironment()
        {
            string revisionValue = Environment.GetEnvironmentVariable("DEVOPSSYNC_EVENT_REVISION");
            string changedFieldsJson = DecodeEventDeltaChangedFieldsJson(
                Environment.GetEnvironmentVariable("DEVOPSSYNC_EVENT_CHANGED_FIELDS_JSON_BASE64"),
                Environment.GetEnvironmentVariable("DEVOPSSYNC_EVENT_CHANGED_FIELDS_JSON"));

            int revisionNumber;
            int? parsedRevision = int.TryParse(revisionValue, out revisionNumber) ? revisionNumber : (int?)null;
            JObject changedFields = ParseEventDeltaChangedFields(changedFieldsJson);

            return new EventDeltaOptions
            {
                RevisionNumber = parsedRevision,
                ChangedFieldsJson = changedFieldsJson,
                ChangedFields = changedFields,
                FieldNames = GetEventDeltaFieldNames(changedFieldsJson)
            };
        }

        private static string DecodeEventDeltaChangedFieldsJson(string changedFieldsJsonBase64, string changedFieldsJson)
        {
            if (string.IsNullOrWhiteSpace(changedFieldsJsonBase64))
            {
                return changedFieldsJson ?? "";
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(changedFieldsJsonBase64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return changedFieldsJson ?? "";
            }
        }

        private static JObject ParseEventDeltaChangedFields(string changedFieldsJson)
        {
            if (string.IsNullOrWhiteSpace(changedFieldsJson))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(changedFieldsJson);
            }
            catch
            {
                return new JObject();
            }
        }

        private static ICollection<string> GetEventDeltaFieldNames(string changedFieldsJson)
        {
            JObject changedFields = ParseEventDeltaChangedFields(changedFieldsJson);
            var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty property in changedFields.Properties())
            {
                if (IsEventDeltaReplayField(property.Name))
                {
                    fieldNames.Add(property.Name);
                }
            }
            return fieldNames;
        }

        private static bool IsEventDeltaReplayField(string referenceName)
        {
            if (string.IsNullOrWhiteSpace(referenceName)) return false;
            if (referenceName == "System.History") return false;
            if (referenceName == "System.Rev") return false;
            if (referenceName == "System.ChangedBy") return false;
            if (referenceName == "System.ChangedDate") return false;
            if (referenceName == "System.AuthorizedAs") return false;
            if (referenceName == "System.AuthorizedDate") return false;
            if (referenceName == "System.RevisedDate") return false;
            if (referenceName == "System.Watermark") return false;
            if (referenceName == "System.CommentCount") return false;
            return true;
        }

        private static string GetEventDeltaValue(JObject changedFields, string referenceName, string propertyName)
        {
            JToken field = changedFields?[referenceName];
            if (field == null)
            {
                return "";
            }
            if (field.Type == JTokenType.Object)
            {
                JToken value = field[propertyName];
                return value == null || value.Type == JTokenType.Null ? "" : value.ToString();
            }
            return propertyName == "newValue" ? field.ToString() : "";
        }

        private static bool IsEventDeltaConflict(string expectedOldValue, string currentTargetValue, string incomingNewValue)
        {
            string expected = NormalizeConflictValue(expectedOldValue);
            string current = NormalizeConflictValue(currentTargetValue);
            string incoming = NormalizeConflictValue(incomingNewValue);

            if (string.Equals(current, expected, StringComparison.Ordinal))
            {
                return false;
            }
            if (string.Equals(current, incoming, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }

        private static string NormalizeConflictValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
        }

        private static bool ShouldPreserveCurrentTargetConflictValue(DateTime incomingChangedDate, DateTime currentTargetChangedDate)
        {
            return currentTargetChangedDate.ToUniversalTime() > incomingChangedDate.ToUniversalTime();
        }

        private static DateTime GetLatestTargetFieldChangedDate(WorkItemData targetWorkItem, string fieldName, DateTime fallbackChangedDate)
        {
            if (targetWorkItem?.Revisions == null || string.IsNullOrWhiteSpace(fieldName))
            {
                return fallbackChangedDate;
            }

            foreach (RevisionItem revision in targetWorkItem.Revisions.Values
                .OrderByDescending(r => r.ChangedDate.ToUniversalTime())
                .ThenByDescending(r => r.Number))
            {
                if (revision?.Fields != null && revision.Fields.ContainsKey(fieldName))
                {
                    return revision.ChangedDate;
                }
            }

            return fallbackChangedDate;
        }

        private static string BuildEventDeltaConflictComment(string fieldName, string expectedOldValue, string currentTargetValue, string incomingNewValue, string appliedValue, string resolution, string direction, int revisionNumber)
        {
            return
                "<span style=\"display:none;\">sync-src:conflict:event-delta</span>" +
                "<b>[Sync conflict resolved]</b><br/>" +
                $"Field: {WebUtility.HtmlEncode(fieldName)}<br/>" +
                $"Direction: {WebUtility.HtmlEncode(direction)}<br/>" +
                $"Revision: {revisionNumber}<br/>" +
                $"Expected previous value: {WebUtility.HtmlEncode(expectedOldValue)}<br/>" +
                $"Conflicting target value: {WebUtility.HtmlEncode(currentTargetValue)}<br/>" +
                $"Incoming value: {WebUtility.HtmlEncode(incomingNewValue)}<br/>" +
                $"Applied latest value: {WebUtility.HtmlEncode(appliedValue)}<br/>" +
                $"Resolution: {WebUtility.HtmlEncode(resolution)}<br/>" +
                "Rule: latest timestamp wins";
        }

        // Comments created before this date are considered already synced (or out of scope).
        // Read from COMMENT_SYNC_CUTOFF file (path in env var) or env var value. No fallback — must be configured.
        private static readonly DateTime CommentSyncCutoffDate = ParseCutoffDate();

        private static DateTime ParseCutoffDate()
        {
            // 1. Try file path from env var
            string filePath = Environment.GetEnvironmentVariable("COMMENT_SYNC_CUTOFF_FILE");
            if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
            {
                string content = System.IO.File.ReadAllText(filePath).Trim();
                if (DateTime.TryParse(content, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime fromFile))
                    return fromFile.ToUniversalTime();
            }
            // 2. Try direct value from env var
            string env = Environment.GetEnvironmentVariable("COMMENT_SYNC_CUTOFF");
            if (!string.IsNullOrEmpty(env) && DateTime.TryParse(env, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed))
                return parsed.ToUniversalTime();
            // 3. No fallback — cutoff date is mandatory
            throw new InvalidOperationException(
                "COMMENT_SYNC_CUTOFF is not configured. Set COMMENT_SYNC_CUTOFF_FILE env var pointing to a file with an ISO 8601 date, or set COMMENT_SYNC_CUTOFF env var directly.");
        }

        private async Task SyncMissingCommentsAsync(WorkItemData sourceWorkItem, WorkItemData targetWorkItem)
        {
            if (sourceWorkItem == null || targetWorkItem == null) return;
            if (!int.TryParse(sourceWorkItem.Id, out int sourceId) || !int.TryParse(targetWorkItem.Id, out int targetId)) return;

            try
            {
                string sourceOrg = GetSourceOrgName();

                var sourceComments = GetCommentsViaApi(
                    Source.Options.Collection.AbsoluteUri, Source.Options.Project,
                    Source.Options.Authentication.AccessToken, sourceId);
                var targetComments = GetCommentsViaApi(
                    Target.Options.Collection.AbsoluteUri, Target.Options.Project,
                    Target.Options.Authentication.AccessToken, targetId);

                // Pass 1: Build map of already-synced source comment IDs → target comment
                var syncedMap = new Dictionary<string, Newtonsoft.Json.Linq.JToken>(StringComparer.OrdinalIgnoreCase);
                foreach (var tc in targetComments)
                {
                    string tcText = tc["text"]?.ToString() ?? "";
                    var markerMatch = SyncSrcMarkerRegex.Match(tcText);
                    if (markerMatch.Success && markerMatch.Groups[1].Value.Equals(sourceOrg, StringComparison.OrdinalIgnoreCase))
                    {
                        syncedMap[markerMatch.Groups[2].Value] = tc;
                    }
                }

                // Find missing, modified, and needs-marker source comments
                var missing = new List<Newtonsoft.Json.Linq.JToken>();
                var modified = new List<(Newtonsoft.Json.Linq.JToken source, Newtonsoft.Json.Linq.JToken target)>();
                var needsMarker = new List<(Newtonsoft.Json.Linq.JToken source, Newtonsoft.Json.Linq.JToken target)>();
                var usedTargetCommentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Pre-mark target comments that already have markers
                foreach (var tc in targetComments)
                {
                    if (SyncSrcMarkerRegex.IsMatch(tc["text"]?.ToString() ?? ""))
                        usedTargetCommentIds.Add(tc["id"]?.ToString() ?? "");
                }

                foreach (var sc in sourceComments)
                {
                    string rawText = sc["text"]?.ToString() ?? "";
                    string commentAuthor = sc["createdBy"]?["displayName"]?.ToString() ?? "";
                    string commentId = sc["id"]?.ToString() ?? "";

                    // Cutoff: ignore comments created before marker-based sync was deployed
                    string createdDateStr = sc["createdDate"]?.ToString();
                    if (DateTime.TryParse(createdDateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime createdDate)
                        && createdDate < CommentSyncCutoffDate)
                        continue;

                    // Anti-loop: skip comments created by sync service accounts (from ANTILOOP_ACCOUNTS env var)
                    string commentAuthorUniqueName = sc["createdBy"]?["uniqueName"]?.ToString() ?? "";
                    bool isSyncAccount = IsAntiloopAccount(commentAuthorUniqueName) || IsAntiloopAccount(commentAuthor);
                    if (isSyncAccount)
                        continue;
                    // Anti-loop: skip comments that are themselves synced copies
                    if (SyncSrcMarkerRegex.IsMatch(rawText))
                        continue;

                    // Marker-based match: check if already synced
                    if (!string.IsNullOrEmpty(commentId) && syncedMap.TryGetValue(commentId, out var targetCopy))
                    {
                        // Check if source was modified after the target copy was created
                        string srcModifiedStr = sc["modifiedDate"]?.ToString();
                        string tgtCreatedStr = targetCopy["createdDate"]?.ToString();
                        if (DateTime.TryParse(srcModifiedStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime srcModified)
                            && DateTime.TryParse(tgtCreatedStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime tgtCreated)
                            && srcModified > tgtCreated)
                        {
                            modified.Add((sc, targetCopy));
                        }
                        continue;
                    }

                    // Content-based fallback: match by normalized content (handles revision replay without markers)
                    string srcNorm = NormalizeCommentForComparison(rawText);
                    bool contentMatched = false;
                    foreach (var tc in targetComments)
                    {
                        string tcId = tc["id"]?.ToString() ?? "";
                        if (usedTargetCommentIds.Contains(tcId)) continue;
                        string tcNorm = NormalizeCommentForComparison(tc["text"]?.ToString() ?? "");
                        if (srcNorm.Equals(tcNorm, StringComparison.OrdinalIgnoreCase))
                        {
                            usedTargetCommentIds.Add(tcId);
                            needsMarker.Add((sc, tc));
                            contentMatched = true;
                            TraceWriteLine(LogEventLevel.Information, " Content-matched source comment {CommentId} to target comment {TargetCommentId}",
                                new Dictionary<string, object>() { { "CommentId", commentId }, { "TargetCommentId", tcId } });
                            break;
                        }
                    }
                    if (contentMatched) continue;

                    missing.Add(sc);
                }

                // Inject markers into content-matched target comments via REST API
                foreach (var (src, tgt) in needsMarker)
                {
                    string srcCId = src["id"]?.ToString() ?? "";
                    string tgtCId = tgt["id"]?.ToString() ?? "";
                    string tgtText = tgt["text"]?.ToString() ?? "";
                    string marker = $"<span style=\"display:none\">sync-src:{WebUtility.HtmlEncode(sourceOrg)}:{srcCId}</span>";
                    await UpdateCommentViaApiAsync(targetId, int.Parse(tgtCId), marker + tgtText);
                    TraceWriteLine(LogEventLevel.Information, " Injected marker into target comment {TargetCommentId} for source {SourceCommentId}",
                        new Dictionary<string, object>() { { "TargetCommentId", tgtCId }, { "SourceCommentId", srcCId } });
                }

                if (missing.Count == 0 && modified.Count == 0)
                {
                    TraceWriteLine(LogEventLevel.Debug, "Comment sync: {SourceCount} source comments on target {TargetWorkItemId} ({MarkerCount} needed marker injection)",
                        new Dictionary<string, object>() { { "SourceCount", sourceComments.Count }, { "TargetWorkItemId", targetWorkItem.Id }, { "MarkerCount", needsMarker.Count } });
                    return;
                }

                // Loop detection: block sync if >5 API syncs in 10 min for this WI pair
                if (IsLoopDetected(sourceId, targetId))
                {
                    TraceWriteLine(LogEventLevel.Error, "Comment sync BLOCKED for {SourceId} -> {TargetId}: loop detected",
                        new Dictionary<string, object>() { { "SourceId", sourceId }, { "TargetId", targetId } });
                    return;
                }

                // Find latest native (non-synced) comment date on target
                DateTime? latestNativeTargetCommentDate = null;
                foreach (var tc in targetComments)
                {
                    string tcText = tc["text"]?.ToString() ?? "";
                    if (SyncSrcMarkerRegex.IsMatch(tcText) || tcText.Contains("[Original date:")) continue;
                    string tcDateStr = tc["createdDate"]?.ToString();
                    if (DateTime.TryParse(tcDateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime tcDate))
                    {
                        latestNativeTargetCommentDate = tcDate;
                        break; // API returns newest first
                    }
                }

                // Post missing comments in chronological order (API returns newest first)
                missing.Reverse();
                foreach (var comment in missing)
                {
                    string commentId = comment["id"]?.ToString() ?? "";
                    string commentAuthorDisplay = comment["createdBy"]?["displayName"]?.ToString();
                    string commentAuthorEmail = comment["createdBy"]?["uniqueName"]?.ToString();
                    string originalText = comment["text"]?.ToString() ?? "";
                    originalText = RewriteCommentImagesForTarget(originalText, targetId);
                    originalText = RewriteCommentWorkItemLinks(originalText);
                    originalText = RewriteCommentMentions(originalText);
                    string marker = $"<span style=\"display:none\">sync-src:{WebUtility.HtmlEncode(sourceOrg)}:{commentId}</span>";

                    // Add original date header only if a native comment on target is more recent
                    // (true interleave). If only field revisions are more recent, skip the header.
                    string originalDateStr = comment["createdDate"]?.ToString();
                    string dateHeader = "";
                    if (DateTime.TryParse(originalDateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime originalDate)
                        && latestNativeTargetCommentDate.HasValue
                        && originalDate < latestNativeTargetCommentDate.Value)
                    {
                        dateHeader = $"<b>[Original date: {originalDate.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC]</b><br>";
                    }

                    // Build author identity string for impersonation via bypassRules
                    string authorIdentity = null;
                    if (!string.IsNullOrWhiteSpace(commentAuthorDisplay))
                    {
                        authorIdentity = !string.IsNullOrWhiteSpace(commentAuthorEmail)
                            ? $"{commentAuthorDisplay} <{commentAuthorEmail}>"
                            : commentAuthorDisplay;
                    }

                    await PostCommentViaApiAsync(targetId, marker + dateHeader + originalText, authorIdentity);
                }

                // Update modified comments on target
                foreach (var (source, target) in modified)
                {
                    string commentId = source["id"]?.ToString() ?? "";
                    string targetCommentId = target["id"]?.ToString() ?? "";
                    string updatedText = source["text"]?.ToString() ?? "";
                    updatedText = RewriteCommentImagesForTarget(updatedText, targetId);
                    updatedText = RewriteCommentWorkItemLinks(updatedText);
                    updatedText = RewriteCommentMentions(updatedText);
                    string marker = $"<span style=\"display:none\">sync-src:{WebUtility.HtmlEncode(sourceOrg)}:{commentId}</span>";

                    string originalDateStr = source["createdDate"]?.ToString();
                    string dateHeader = "";
                    if (DateTime.TryParse(originalDateStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime originalDate))
                    {
                        dateHeader = $"<b>[Original date: {originalDate.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC]</b><br>";
                    }

                    await UpdateCommentViaApiAsync(targetId, int.Parse(targetCommentId), marker + dateHeader + updatedText);
                }

                int totalActions = missing.Count + modified.Count;
                TraceWriteLine(LogEventLevel.Information, "Comment sync: {MissingCount} posted, {ModifiedCount} updated on {TargetWorkItemId}",
                    new Dictionary<string, object>() { { "MissingCount", missing.Count }, { "ModifiedCount", modified.Count }, { "TargetWorkItemId", targetWorkItem.Id } });

                if (totalActions > 0)
                {
                    string alertAction = missing.Count > 0 && modified.Count > 0
                        ? $"Posted {missing.Count} + Updated {modified.Count}"
                        : missing.Count > 0 ? $"Posted {missing.Count}" : $"Updated {modified.Count}";
                    PostAlertToMonitoringWI(alertAction, sourceId, targetId, totalActions);
                }
            }
            catch (Exception ex)
            {
                TraceWriteLine(LogEventLevel.Warning, "Comment sync failed for {TargetWorkItemId}: {ErrorMessage}",
                    new Dictionary<string, object>() { { "TargetWorkItemId", targetWorkItem.Id }, { "ErrorMessage", ex.Message } });
            }
        }

        private string RewriteCommentImagesForTarget(string commentHtml, int targetWorkItemId)
        {
            if (string.IsNullOrWhiteSpace(commentHtml)) return commentHtml;

            string sourceOrg = Source.Options.Collection.AbsoluteUri.TrimEnd('/');
            string sourcePat = Source.Options.Authentication.AccessToken;
            string targetPat = Target.Options.Authentication.AccessToken;
            string targetBaseUri = Target.Options.Collection.AbsoluteUri.TrimEnd('/');
            string targetProject = Uri.EscapeDataString(Target.Options.Project);

            // Match attachment URLs from source org
            var attachmentRegex = new System.Text.RegularExpressions.Regex(
                @"https?://dev\.azure\.com/[^""'\s]+/_apis/wit/attachments/[^""'\s]+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = attachmentRegex.Matches(commentHtml);
            if (matches.Count == 0) return commentHtml;

            using (var downloadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                downloadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{sourcePat}")));

                using (var uploadClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    uploadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{targetPat}")));

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        string originalUrl = WebUtility.HtmlDecode(match.Value);
                        // Only rewrite URLs from the source org
                        if (originalUrl.IndexOf(sourceOrg, StringComparison.OrdinalIgnoreCase) < 0 &&
                            !originalUrl.ToLowerInvariant().Contains(Source.Options.Collection.Host.ToLowerInvariant()))
                            continue;

                        try
                        {
                            // Download from source
                            var imageBytes = downloadClient.GetByteArrayAsync(originalUrl).Result;
                            // Extract filename from URL
                            var fileNameMatch = System.Text.RegularExpressions.Regex.Match(originalUrl, @"fileName=([^&\s]+)");
                            string fileName = fileNameMatch.Success ? fileNameMatch.Groups[1].Value : "image.png";

                            // Upload to target
                            string uploadUrl = $"{targetBaseUri}/{targetProject}/_apis/wit/attachments?fileName={Uri.EscapeDataString(fileName)}&api-version=7.1";
                            using (var uploadContent = new ByteArrayContent(imageBytes))
                            {
                                uploadContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                                var uploadResponse = uploadClient.PostAsync(uploadUrl, uploadContent).Result;
                                uploadResponse.EnsureSuccessStatusCode();
                                var responseJson = Newtonsoft.Json.Linq.JObject.Parse(uploadResponse.Content.ReadAsStringAsync().Result);
                                string newUrl = responseJson["url"]?.ToString();
                                if (!string.IsNullOrEmpty(newUrl))
                                {
                                    commentHtml = commentHtml.Replace(match.Value, newUrl);
                                    commentHtml = commentHtml.Replace(WebUtility.HtmlEncode(match.Value), newUrl);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning("Failed to rewrite image {Url} in comment: {Error}", originalUrl, ex.Message);
                        }
                    }
                }
            }
            return commentHtml;
        }

        private async Task PostCommentViaApiAsync(int targetWorkItemId, string commentHtml, string originalAuthor = null)
        {
            string project = Uri.EscapeDataString(Target.Options.Project);
            string baseUri = Target.Options.Collection.AbsoluteUri.TrimEnd('/');
            string token = Target.Options.Authentication.AccessToken;

            // Use Work Item PATCH API with bypassRules to impersonate the original author
            string requestUri = $"{baseUri}/{project}/_apis/wit/workitems/{targetWorkItemId}?bypassRules=true&api-version=7.0";
            Log.LogInformation("PostCommentViaApiAsync: PATCH {RequestUri} with bypassRules, author={Author}", requestUri, originalAuthor ?? "(none)");

            var patchOps = new Newtonsoft.Json.Linq.JArray();
            patchOps.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["op"] = "add",
                ["path"] = "/fields/System.History",
                ["value"] = commentHtml
            });

            if (!string.IsNullOrWhiteSpace(originalAuthor))
            {
                patchOps.Add(new Newtonsoft.Json.Linq.JObject
                {
                    ["op"] = "add",
                    ["path"] = "/fields/System.ChangedBy",
                    ["value"] = originalAuthor
                });
            }

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));
                using (var content = new StringContent(patchOps.ToString(), Encoding.UTF8, "application/json-patch+json"))
                {
                    // Explicit PATCH method — HttpClient.PatchAsync may not exist on net472
                    var request = new HttpRequestMessage(new HttpMethod("PATCH"), requestUri) { Content = content };
                    var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        /// <summary>
        /// Returns true if a loop is detected (>5 API syncs in 10 minutes for the same WI pair).
        /// When detected, posts a LOOP ALERT and blocks further sync.
        /// </summary>
        private bool IsLoopDetected(int sourceWorkItemId, int targetWorkItemId)
        {
            string key = $"{sourceWorkItemId}->{targetWorkItemId}";
            lock (_trackerLock)
            {
                DateTime now = DateTime.UtcNow;
                if (_apiSyncTracker.TryGetValue(key, out var entry))
                {
                    if ((now - entry.firstSeen).TotalMinutes <= 10)
                    {
                        int newCount = entry.count + 1;
                        _apiSyncTracker[key] = (newCount, entry.firstSeen);
                        if (newCount > 5)
                        {
                            TraceWriteLine(LogEventLevel.Error, "LOOP DETECTED: {Count} API syncs in {Minutes:F1} min for {SourceId} -> {TargetId}",
                                new Dictionary<string, object>() {
                                    { "Count", newCount },
                                    { "Minutes", (now - entry.firstSeen).TotalMinutes },
                                    { "SourceId", sourceWorkItemId },
                                    { "TargetId", targetWorkItemId }
                                });
                            PostAlertToMonitoringWI(
                                $"LOOP DETECTED: {newCount} API syncs in {(now - entry.firstSeen).TotalMinutes:F0} min — sync BLOCKED",
                                sourceWorkItemId, targetWorkItemId, newCount);
                            return true;
                        }
                    }
                    else
                    {
                        // Window expired, reset
                        _apiSyncTracker[key] = (1, now);
                    }
                }
                else
                {
                    _apiSyncTracker[key] = (1, now);
                }
            }
            return false;
        }

        private void PostAlertToMonitoringWI(string action, int sourceWorkItemId, int targetWorkItemId, int commentCount)
        {
            // Alert via @mention on a monitoring WI. Configured via env vars:
            // ALERT_ORG, ALERT_PROJECT, ALERT_WI_ID, ALERT_PAT, ALERT_MENTION_GUID, ALERT_MENTION_NAME
            string alertOrg = Environment.GetEnvironmentVariable("ALERT_ORG");
            string alertProject = Environment.GetEnvironmentVariable("ALERT_PROJECT");
            string alertWiId = Environment.GetEnvironmentVariable("ALERT_WI_ID");
            string alertPat = Environment.GetEnvironmentVariable("ALERT_PAT");
            string mentionGuid = Environment.GetEnvironmentVariable("ALERT_MENTION_GUID");
            string mentionName = Environment.GetEnvironmentVariable("ALERT_MENTION_NAME");
            string alertClient = Environment.GetEnvironmentVariable("ALERT_CLIENT") ?? "";

            if (string.IsNullOrEmpty(alertOrg) || string.IsNullOrEmpty(alertWiId) || string.IsNullOrEmpty(alertPat))
                return;

            try
            {
                string sourceOrg = GetSourceOrgName();
                string sourceProject = Source.Options.Project;
                string targetOrg = Target.Options.Collection.AbsoluteUri.TrimEnd('/');
                string targetProject = Target.Options.Project;
                string mention = !string.IsNullOrEmpty(mentionGuid)
                    ? $"<a href=\"#\" data-vss-mention=\"version:2.0,{mentionGuid}\">@{WebUtility.HtmlEncode(mentionName ?? "Alert")}</a> "
                    : "";
                string clientTag = !string.IsNullOrEmpty(alertClient) ? $"[{alertClient.ToUpper()}] " : "";

                string commentText = $"{mention}<b>[SYNC ALERT]</b> {clientTag}{action}: " +
                    $"<b>{commentCount}</b> comment(s) synced via API from " +
                    $"<b>{sourceOrg}/{sourceProject} WI#{sourceWorkItemId}</b> → " +
                    $"<b>{targetOrg}/{targetProject} WI#{targetWorkItemId}</b> " +
                    $"at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";

                string requestUri = $"https://dev.azure.com/{alertOrg}/{Uri.EscapeDataString(alertProject ?? alertOrg)}/_apis/wit/workitems/{alertWiId}?api-version=7.0";
                var patchOps = new Newtonsoft.Json.Linq.JArray();
                patchOps.Add(new Newtonsoft.Json.Linq.JObject
                {
                    ["op"] = "add",
                    ["path"] = "/fields/System.History",
                    ["value"] = commentText
                });

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{alertPat}")));
                    using (var content = new StringContent(patchOps.ToString(), Encoding.UTF8, "application/json-patch+json"))
                    {
                        var request = new HttpRequestMessage(new HttpMethod("PATCH"), requestUri) { Content = content };
                        client.SendAsync(request).GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed to post alert to monitoring WI: {Error}", ex.Message);
            }
        }

        private async Task UpdateCommentViaApiAsync(int targetWorkItemId, int targetCommentId, string commentHtml)
        {
            string project = Uri.EscapeDataString(Target.Options.Project);
            string baseUri = Target.Options.Collection.AbsoluteUri.TrimEnd('/');
            string token = Target.Options.Authentication.AccessToken;

            string requestUri = $"{baseUri}/{project}/_apis/wit/workItems/{targetWorkItemId}/comments/{targetCommentId}?api-version=7.1-preview.4";
            Log.LogInformation("UpdateCommentViaApiAsync: PATCH comment {CommentId} on WI {WorkItemId}", targetCommentId, targetWorkItemId);

            var body = new Newtonsoft.Json.Linq.JObject { ["text"] = commentHtml };
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));
                using (var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json"))
                {
                    var request = new HttpRequestMessage(new HttpMethod("PATCH"), requestUri) { Content = content };
                    var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        private void SetReflectedWorkItemIdViaApi(string collectionUri, string project, string pat,
            int workItemId, string fieldReferenceName, string reflectedValue)
        {
            string baseUri = collectionUri.TrimEnd('/');
            string requestUri = $"{baseUri}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/{workItemId}?api-version=7.0";

            var patchOps = new Newtonsoft.Json.Linq.JArray();
            patchOps.Add(new Newtonsoft.Json.Linq.JObject
            {
                ["op"] = "add",
                ["path"] = $"/fields/{fieldReferenceName}",
                ["value"] = reflectedValue
            });

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
                using (var content = new StringContent(patchOps.ToString(), Encoding.UTF8, "application/json-patch+json"))
                {
                    var request = new HttpRequestMessage(new HttpMethod("PATCH"), requestUri) { Content = content };
                    var response = client.SendAsync(request).GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        private bool TrySaveOrFallbackComment(WorkItemData targetWorkItem, RevisionItem revision, string historyValue, ref DateTime lastSavedDate)
        {
            try
            {
                targetWorkItem.SaveToAzureDevOps();
                // Use the historical date we SET on the revision, not what the server returned.
                // After a bypassRules save the server honours the historical date on the revision
                // but the SOAP WorkItem object may report the server clock as System.ChangedDate.
                // Using the server clock would make lastSavedDate jump ahead of subsequent
                // source revision dates, triggering false VS402625 bumps on the next iteration.
                lastSavedDate = revision.ChangedDate;
                return true;
            }
            catch (Exception ex) when (ex.ToString().Contains("VS402625") || ex.ToString().Contains("VS402624"))
            {
                string commentText = historyValue?.Trim();
                if (string.IsNullOrEmpty(commentText))
                {
                    Log.LogWarning("VS402625 on revision {RevisionNumber} with no comment - skipping", revision.Number);
                    return false;
                }

                string author = revision.Fields.ContainsKey("System.ChangedBy")
                    ? revision.Fields["System.ChangedBy"].Value?.ToString() ?? "Unknown"
                    : "Unknown";
                DateTime originalDate = revision.OriginalChangedDate == default ? revision.ChangedDate : revision.OriginalChangedDate;

                // Clear the history and re-save without it
                targetWorkItem.ToWorkItem().Fields["System.History"].Value = null;
                try
                {
                    targetWorkItem.SaveToAzureDevOps();
                    lastSavedDate = revision.ChangedDate;
                }
                catch
                {
                    Log.LogWarning("VS402625 fallback: field-only save also failed for revision {RevisionNumber} - skipping", revision.Number);
                    return false;
                }

                // Post comment via REST API (VS402625 fallback — revision replay failed)
                int targetId = int.Parse(targetWorkItem.Id);
                string dateHeader = $"<b>[Original date: {originalDate.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC]</b><br>";
                PostCommentViaApiAsync(targetId, dateHeader + commentText, author).GetAwaiter().GetResult();

                TraceWriteLine(LogEventLevel.Warning,
                    "VS402625 on revision {RevisionNumber}: comment posted via API fallback for TargetWorkItem {TargetWorkItemId}",
                    new Dictionary<string, object>() {
                        { "RevisionNumber", revision.Number },
                        { "TargetWorkItemId", targetWorkItem.Id }
                    });
                return true;
            }
        }

        private WorkItemData ApplyEventDelta(EventDeltaOptions eventDelta, WorkItemData sourceWorkItem, WorkItemData targetWorkItem)
        {
            if (!eventDelta.RevisionNumber.HasValue)
            {
                throw new InvalidOperationException("Event delta mode requires DEVOPSSYNC_EVENT_REVISION.");
            }

            RevisionItem revision = sourceWorkItem.Revisions.Values.FirstOrDefault(r => r.Number == eventDelta.RevisionNumber.Value);
            if (revision == null)
            {
                throw new InvalidOperationException($"Event delta revision {eventDelta.RevisionNumber.Value} was not found on source work item {sourceWorkItem.Id}.");
            }

            if (IsSyncGeneratedRevision(revision))
            {
                TraceWriteLine(LogEventLevel.Information, " Skipping sync-generated event delta revision [{RevisionNumber}]",
                    new Dictionary<string, object>() { { "RevisionNumber", revision.Number } });
                return targetWorkItem;
            }

            var currentRevisionWorkItem = sourceWorkItem.GetRevision(revision.Number);
            var destType = currentRevisionWorkItem.Type;
            if (CommonTools.WorkItemTypeMapping.Mappings.ContainsKey(destType))
            {
                destType = CommonTools.WorkItemTypeMapping.Mappings[destType];
            }

            DateTime lastSavedDate = targetWorkItem?.ToWorkItem()?.Fields["System.ChangedDate"]?.Value is DateTime d ? d : DateTime.MinValue;
            var fieldsToSkip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> conflictComments = BuildEventDeltaConflictComments(eventDelta, targetWorkItem, revision.Number, revision.ChangedDate, lastSavedDate, fieldsToSkip);
            var fieldsToApply = new HashSet<string>(eventDelta.FieldNames, StringComparer.OrdinalIgnoreCase);
            foreach (string fieldName in fieldsToSkip)
            {
                fieldsToApply.Remove(fieldName);
            }

            PopulateWorkItem(currentRevisionWorkItem, targetWorkItem, destType, applyHistoryViaObjectModel: false, fieldFilter: fieldsToApply);

            if (revision.ChangedDate <= lastSavedDate)
            {
                revision.ChangedDate = lastSavedDate.AddSeconds(1);
            }
            DateTime nowUtc = DateTime.UtcNow;
            if (revision.ChangedDate > nowUtc)
            {
                revision.ChangedDate = nowUtc;
            }

            targetWorkItem.ToWorkItem().Fields["System.ChangedDate"].Value = revision.ChangedDate;
            if (revision.Fields.ContainsKey("System.ChangedBy"))
            {
                targetWorkItem.ToWorkItem().Fields["System.ChangedBy"].Value = revision.Fields["System.ChangedBy"].Value.ToString();
            }

            if (conflictComments.Count > 0)
            {
                targetWorkItem.ToWorkItem().Fields["System.History"].Value = string.Join("<br/>", conflictComments);
            }

            var reflectedUri = (TfsReflectedWorkItemId)Source.WorkItems.CreateReflectedWorkItemId(sourceWorkItem);
            if (!targetWorkItem.ToWorkItem().Fields.Contains(Target.Options.ReflectedWorkItemIdField))
            {
                throw new InvalidOperationException("ReflectedWorkItemIdField Field Missing");
            }
            targetWorkItem.ToWorkItem().Fields[Target.Options.ReflectedWorkItemIdField].Value = reflectedUri.ToString();

            ProcessHTMLFieldAttachements(targetWorkItem);
            ProcessWorkItemEmbeddedLinks(sourceWorkItem, targetWorkItem);
            CheckClosedDateIsValid(sourceWorkItem, targetWorkItem);

            if (!SkipRevisionWithInvalidIterationPath(targetWorkItem) && !SkipRevisionWithInvalidAreaPath(targetWorkItem))
            {
                string historyForFallback = targetWorkItem.ToWorkItem().Fields["System.History"].Value?.ToString();
                TrySaveOrFallbackComment(targetWorkItem, revision, historyForFallback, ref lastSavedDate);
            }

            TraceWriteLine(LogEventLevel.Information,
                " Saved TargetWorkItem {TargetWorkItemId}. Applied event delta revision {RevisionNumber}",
                new Dictionary<string, object>()
                {
                    { "TargetWorkItemId", targetWorkItem.Id },
                    { "RevisionNumber", revision.Number }
                });

            return targetWorkItem;
        }

        private List<string> BuildEventDeltaConflictComments(EventDeltaOptions eventDelta, WorkItemData targetWorkItem, int revisionNumber, DateTime incomingChangedDate, DateTime currentTargetChangedDate, ICollection<string> fieldsToSkip)
        {
            var comments = new List<string>();
            WorkItem target = targetWorkItem.ToWorkItem();
            foreach (string fieldName in eventDelta.FieldNames)
            {
                if (!target.Fields.Contains(fieldName) || !EventDeltaHasValue(eventDelta.ChangedFields, fieldName, "oldValue"))
                {
                    continue;
                }

                string expectedOldValue = GetEventDeltaValue(eventDelta.ChangedFields, fieldName, "oldValue");
                string incomingNewValue = GetEventDeltaValue(eventDelta.ChangedFields, fieldName, "newValue");
                string currentTargetValue = target.Fields[fieldName].Value?.ToString() ?? "";

                if (IsEventDeltaConflict(expectedOldValue, currentTargetValue, incomingNewValue))
                {
                    DateTime currentTargetFieldChangedDate = GetLatestTargetFieldChangedDate(targetWorkItem, fieldName, currentTargetChangedDate);
                    bool preserveTargetValue = ShouldPreserveCurrentTargetConflictValue(incomingChangedDate, currentTargetFieldChangedDate);
                    string appliedValue = preserveTargetValue ? currentTargetValue : incomingNewValue;
                    string resolution = preserveTargetValue ? "newer target value preserved" : "latest incoming event applied";
                    if (preserveTargetValue)
                    {
                        fieldsToSkip.Add(fieldName);
                    }
                    comments.Add(BuildEventDeltaConflictComment(fieldName, expectedOldValue, currentTargetValue, incomingNewValue, appliedValue, resolution, Options.SourceName, revisionNumber));
                }
            }
            return comments;
        }

        private static bool EventDeltaHasValue(JObject changedFields, string referenceName, string propertyName)
        {
            JToken field = changedFields?[referenceName];
            return field?.Type == JTokenType.Object && field[propertyName] != null && field[propertyName].Type != JTokenType.Null;
        }

        private WorkItemData ReplayRevisions(List<RevisionItem> revisionsToMigrate, WorkItemData sourceWorkItem, WorkItemData targetWorkItem)
        {
            try
            {
                bool targetCreatedForReplay = targetWorkItem == null;

                //If work item hasn't been created yet, create a shell
                if (targetWorkItem == null)
                {
                    var finalDestType = revisionsToMigrate.Last().Type;
                    var targetType = revisionsToMigrate.First().Type;

                    if (targetType != finalDestType)
                    {
                        TraceWriteLine(LogEventLevel.Information, $"WorkItem has changed type at one of the revisions, from {targetType} to {finalDestType}");
                    }

                    if (CommonTools.WorkItemTypeMapping.Mappings.ContainsKey(targetType))
                    {
                        targetType = CommonTools.WorkItemTypeMapping.Mappings[targetType];
                    }
                    targetWorkItem = CreateWorkItem_Shell(Target.WorkItems.Project, sourceWorkItem, targetType);
                }

                if (Options.AttachRevisionHistory)
                {
                    CommonTools.RevisionManager.AttachSourceRevisionHistoryJsonToTarget(sourceWorkItem, targetWorkItem);
                }

                // Track the historical date we SET on each revision (not the server's response).
                // After a bypassRules save, the server honours the historical ChangedDate on
                // the revision but the SOAP WorkItem object may report the server clock instead.
                // Using the server clock would make lastSavedDate jump ahead of subsequent
                // source revision dates, causing false VS402625 bumps.
                DateTime lastSavedDate = targetWorkItem?.ToWorkItem()?.Fields["System.ChangedDate"]?.Value is DateTime d ? d : DateTime.MinValue;

                foreach (var revision in revisionsToMigrate)
                {
                    workItemMetrics.RevisionsProcessedCount.Add(1);

                    // Skip sync-generated revisions to prevent bidirectional loops
                    string revChangedBy = revision.Fields.ContainsKey("System.ChangedBy")
                        ? revision.Fields["System.ChangedBy"].Value?.ToString() ?? ""
                        : "";
                    bool isSyncGenerated = IsSyncGeneratedRevision(revision);
                    if (isSyncGenerated)
                    {
                        TraceWriteLine(LogEventLevel.Information, " Skipping sync-generated revision [{RevisionNumber}] (ChangedBy: {ChangedBy})",
                            new Dictionary<string, object>() {
                                {"RevisionNumber", revision.Number },
                                {"ChangedBy", revChangedBy }
                            });
                        continue;
                    }

                    var currentRevisionWorkItem = sourceWorkItem.GetRevision(revision.Number);

                    TraceWriteLine(LogEventLevel.Information, " Processing Revision [{RevisionNumber}]",
                        new Dictionary<string, object>() {
                            {"RevisionNumber", revision.Number }
                        });

                    // Decide on WIT
                    var destType = currentRevisionWorkItem.Type;
                    if (CommonTools.WorkItemTypeMapping.Mappings.ContainsKey(destType))
                    {
                        destType = CommonTools.WorkItemTypeMapping.Mappings[destType];
                    }
                    bool typeChange = (destType != targetWorkItem.Type);

                    int workItemId = Int32.Parse(targetWorkItem.Id);

                    if (typeChange && workItemId > 0)
                    {
                        ValidatePatTokenRequirement();
                        Uri collectionUri = Target.Options.Collection;
                        string token = Target.Options.Authentication.AccessToken;
                        VssConnection connection = new VssConnection(collectionUri, new VssBasicCredential(string.Empty, token));
                        WorkItemTrackingHttpClient workItemTrackingClient = connection.GetClient<WorkItemTrackingHttpClient>();
                        JsonPatchDocument patchDocument = new JsonPatchDocument();
                        // Use a date slightly before the revision date for the type-change intermediate revision.
                        // This ensures the subsequent SOAP save (with the actual revision date) is strictly after.
                        DateTime typeChangeDate = ((DateTime)currentRevisionWorkItem.Fields["System.ChangedDate"].Value).AddMilliseconds(-3);

                        // Ensure the type-change date is strictly after the last persisted date
                        if (typeChangeDate <= lastSavedDate)
                        {
                            typeChangeDate = lastSavedDate.AddSeconds(1);
                            revision.ChangedDate = typeChangeDate.AddSeconds(1);
                        }

                        patchDocument.Add(
                            new JsonPatchOperation()
                            {
                                Operation = Operation.Add,
                                Path = "/fields/System.WorkItemType",
                                Value = destType
                            }
                        );
                        patchDocument.Add(
                            new JsonPatchOperation()
                            {
                                Operation = Operation.Add,
                                Path = "/fields/System.State",
                                Value = (string)currentRevisionWorkItem.Fields["System.State"].Value
                            }
                        );
                        patchDocument.Add(
                            new JsonPatchOperation()
                            {
                                Operation = Operation.Add,
                                Path = "/fields/System.Reason",
                                Value = (string)currentRevisionWorkItem.Fields["System.Reason"].Value
                            }
                        );
                        patchDocument.Add(
                            new JsonPatchOperation()
                            {
                                Operation = Operation.Add,
                                Path = "/fields/System.ChangedDate",
                                Value = typeChangeDate
                            }
                        );
                        patchDocument.Add(
                        new JsonPatchOperation()
                        {
                            Operation = Operation.Add,
                            Path = "/fields/System.ChangedBy",
                            Value = currentRevisionWorkItem.Fields["System.ChangedBy"].Value.ToString()
                        }
                        );
                        var result = workItemTrackingClient.UpdateWorkItemAsync(patchDocument, workItemId, bypassRules: true).Result;
                        targetWorkItem = Target.WorkItems.GetWorkItem(workItemId);
                        lastSavedDate = typeChangeDate;
                    }
                    bool applyHistoryViaObjectModel = ShouldApplyHistoryViaObjectModel(revision, CommentSyncCutoffDate);
                    bool hasObjectModelReplayChanges = typeChange || RevisionHasObjectModelReplayChanges(revision, _ignore);
                    if (!targetCreatedForReplay && !applyHistoryViaObjectModel && !hasObjectModelReplayChanges)
                    {
                        TraceWriteLine(LogEventLevel.Information, " Skipped Object Model replay for revision {RevisionNumber}; only API-owned comment metadata changed",
                            new Dictionary<string, object>() { { "RevisionNumber", revision.Number } });
                        continue;
                    }

                    PopulateWorkItem(currentRevisionWorkItem, targetWorkItem, destType, applyHistoryViaObjectModel);

                    var fails = ((WorkItem)targetWorkItem.internalObject).Validate();
                    foreach (Field f in fails)
                    {
                        if (f.Name == "Reason")
                        {
                            if (f.AllowedValues.Count > 0)
                            {
                                targetWorkItem.ToWorkItem().Fields[f.Name].Value = f.AllowedValues[0];
                            }
                            else if (f.FieldDefinition.AllowedValues.Count > 0)
                            {
                                targetWorkItem.ToWorkItem().Fields[f.Name].Value = f.FieldDefinition.AllowedValues[0];
                            }
                        }
                    }
                    // Impersonate revision author.
                    // Ensure revision date is strictly after the historical date we set on the
                    // previous revision (VS402625 fix). lastSavedDate tracks what we SET, not
                    // what the server clock was, so the bump only triggers when source dates
                    // genuinely collide — not because the server save took wall-clock time.
                    // Also clamp to not exceed current time (VS402624 fix).
                    if (revision.ChangedDate <= lastSavedDate)
                    {
                        revision.ChangedDate = lastSavedDate.AddSeconds(1);
                    }
                    DateTime nowUtc = DateTime.UtcNow;
                    if (revision.ChangedDate > nowUtc)
                    {
                        revision.ChangedDate = nowUtc;
                    }
                    targetWorkItem.ToWorkItem().Fields["System.ChangedDate"].Value = revision.ChangedDate;
                    targetWorkItem.ToWorkItem().Fields["System.ChangedBy"].Value = revision.Fields["System.ChangedBy"].Value.ToString();
                    if (applyHistoryViaObjectModel)
                    {
                        targetWorkItem.ToWorkItem().Fields["System.History"].Value = revision.Fields["System.History"].Value;
                    }
                    else if (revision.Fields.ContainsKey("System.History"))
                    {
                        targetWorkItem.ToWorkItem().Fields["System.History"].Value = null;
                        TraceWriteLine(LogEventLevel.Information, " Skipped System.History in Object Model replay for revision {RevisionNumber}; comments are handled by API sync",
                            new Dictionary<string, object>() { { "RevisionNumber", revision.Number } });
                    }

                    // Todo: Ensure all field maps use WorkItemData.Fields to apply a correct mapping
                    CommonTools.FieldMappingTool.ApplyFieldMappings(currentRevisionWorkItem, targetWorkItem);

                    // Todo: Think about an "UpdateChangedBy" flag as this is expensive! (2s/WI instead of 1,5s when writing "Migration")

                    var reflectedUri = (TfsReflectedWorkItemId)Source.WorkItems.CreateReflectedWorkItemId(sourceWorkItem);
                    if (!targetWorkItem.ToWorkItem().Fields.Contains(Target.Options.ReflectedWorkItemIdField))
                    {
                        var ex = new InvalidOperationException("ReflectedWorkItemIdField Field Missing");
                        Log.LogError(ex,
                            " The WorkItemType {WorkItemType} does not have a Field called {ReflectedWorkItemID}",
                            targetWorkItem.Type,
                            Target.Options.ReflectedWorkItemIdField);
                        throw ex;
                    }
                    targetWorkItem.ToWorkItem().Fields[Target.Options.ReflectedWorkItemIdField].Value = reflectedUri.ToString();

                    ProcessHTMLFieldAttachements(targetWorkItem);
                    ProcessWorkItemEmbeddedLinks(sourceWorkItem, targetWorkItem);

                    var skipIterationRevision = SkipRevisionWithInvalidIterationPath(targetWorkItem);
                    var skipAreaRevision = SkipRevisionWithInvalidAreaPath(targetWorkItem);

                    CheckClosedDateIsValid(sourceWorkItem, targetWorkItem);

                    if (!skipIterationRevision && !skipAreaRevision)
                    {
                        string historyForFallback = targetWorkItem.ToWorkItem().Fields["System.History"].Value?.ToString();
                        TrySaveOrFallbackComment(targetWorkItem, revision, historyForFallback, ref lastSavedDate);
                    }
                    TraceWriteLine(LogEventLevel.Information,
                        " Saved TargetWorkItem {TargetWorkItemId}. Replayed revision {RevisionNumber} of {RevisionsToMigrateCount}",
                       new Dictionary<string, object>() {
                               {"TargetWorkItemId", targetWorkItem.Id },
                               {"RevisionNumber", revision.Number },
                               {"RevisionsToMigrateCount",  revisionsToMigrate.Count}
                           });
                }

                // Until here we impersonate the maker of the revisions. From here we act as ourselves to push the attachments and add the comment
                if (targetWorkItem != null)
                {
                    ProcessWorkItemAttachments(sourceWorkItem, targetWorkItem, false);
                    if (!string.IsNullOrEmpty(targetWorkItem.Id))
                    {
                        ProcessWorkItemLinks(sourceWorkItem, targetWorkItem);
                    }

                    if (Options.GenerateMigrationComment)
                    {
                        var reflectedUri = targetWorkItem.ToWorkItem().Fields[Target.Options.ReflectedWorkItemIdField].Value;
                        var history = new StringBuilder();
                        history.Append(
                            $"This work item was migrated from a different project or organization. You can find the old version at <a href=\"{reflectedUri}\">{reflectedUri}</a>.");
                        targetWorkItem.ToWorkItem().History = history.ToString();
                    }

                    if (targetWorkItem.ToWorkItem().IsDirty)
                    {
                        targetWorkItem.ToWorkItem().Fields["System.ChangedBy"].Value = "Migration";
                        targetWorkItem.ToWorkItem().Fields["System.ChangedDate"].Value = lastSavedDate.AddSeconds(1);
                        targetWorkItem.SaveToAzureDevOps();
                        TraceWriteLine(LogEventLevel.Information, "...Saved as {TargetWorkItemId}", new Dictionary<string, object> { { "TargetWorkItemId", targetWorkItem.Id } });
                    }
                    else
                    {
                        TraceWriteLine(LogEventLevel.Information, "...Skipped save for {TargetWorkItemId}, no changes detected", new Dictionary<string, object> { { "TargetWorkItemId", targetWorkItem.Id } });
                    }

                    CommonTools.Attachment.CleanUpAfterSave();

                    // Set ReflectedWorkItemId on the SOURCE work item (counterpart)
                    // so both sides know about each other. Uses REST API since source
                    // is in a different org/project.
                    try
                    {
                        var targetReflectedUri = (TfsReflectedWorkItemId)Target.WorkItems.CreateReflectedWorkItemId(targetWorkItem);
                        int sourceId = int.Parse(sourceWorkItem.Id);
                        string sourceReflectedField = Source.Options.ReflectedWorkItemIdField;
                        string currentSourceReflected = sourceWorkItem.ToWorkItem().Fields.Contains(sourceReflectedField)
                            ? sourceWorkItem.ToWorkItem().Fields[sourceReflectedField].Value?.ToString() ?? ""
                            : "";
                        string targetReflectedValue = targetReflectedUri.ToString();

                        if (!targetReflectedValue.Equals(currentSourceReflected, StringComparison.OrdinalIgnoreCase))
                        {
                            SetReflectedWorkItemIdViaApi(
                                Source.Options.Collection.AbsoluteUri,
                                Source.Options.Project,
                                Source.Options.Authentication.AccessToken,
                                sourceId,
                                sourceReflectedField,
                                targetReflectedValue);
                            TraceWriteLine(LogEventLevel.Information,
                                "Set ReflectedWorkItemId on source WI#{SourceId} → {TargetReflectedUri}",
                                new Dictionary<string, object>() {
                                    { "SourceId", sourceId },
                                    { "TargetReflectedUri", targetReflectedValue }
                                });
                        }
                    }
                    catch (Exception reflEx)
                    {
                        Log.LogWarning("Failed to set ReflectedWorkItemId on source WI#{SourceId}: {Error}",
                            sourceWorkItem.Id, reflEx.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Dictionary<string, string> parameters = new Dictionary<string, string>();
                if (targetWorkItem != null)
                {
                    foreach (Field f in targetWorkItem.ToWorkItem().Fields)
                        parameters.Add($"{f.ReferenceName} ({f.Name})", f.Value?.ToString());
                }
                Telemetry.TrackException(ex, parameters);
                TraceWriteLine(LogEventLevel.Information, "...FAILED to Save");
                Log.LogInformation("===============================================================");
                if (targetWorkItem != null)
                {
                    foreach (Field f in targetWorkItem.ToWorkItem().Fields)
                        TraceWriteLine(LogEventLevel.Information, "{FieldReferenceName} ({FieldName}) | {FieldValue}",
                            new Dictionary<string, object>()
                            {
                                { "FieldReferenceName", f.ReferenceName }, { "FieldName", f.Name },
                                { "FieldValue", f.Value }
                            });
                }
                Log.LogInformation("===============================================================");
                Log.LogError(ex.ToString(), ex);
                Log.LogInformation("===============================================================");
            }

            return targetWorkItem;
        }

        private void CheckClosedDateIsValid(WorkItemData sourceWorkItem, WorkItemData targetWorkItem)
        {
            var closedDateField = "System.ClosedDate";
            if (targetWorkItem.ToWorkItem().Fields.Contains("Microsoft.VSTS.Common.ClosedDate"))
            {
                closedDateField = "Microsoft.VSTS.Common.ClosedDate";
            }
            else if (!targetWorkItem.ToWorkItem().Fields.Contains("System.ClosedDate"))
            {
                Log.LogDebug("CheckClosedDateIsValid::ClosedDate field doesn't exist in targetWorkItem: {targetWorkItem} - nothing to validate.", targetWorkItem);
                return;
            }

            Log.LogDebug("CheckClosedDateIsValid::ClosedDate field is {closedDateField}", closedDateField);
            if (targetWorkItem.ToWorkItem().Fields[closedDateField].Value == null && (targetWorkItem.ToWorkItem().Fields["System.State"].Value.ToString() == "Closed" || targetWorkItem.ToWorkItem().Fields["System.State"].Value.ToString() == "Done"))
            {
                Log.LogWarning("The field {closedDateField} is set to Null and will revert to the current date on save! ", closedDateField);
                if (sourceWorkItem.ToWorkItem().Fields.Contains(closedDateField))
                {
                    Log.LogWarning("Source Closed Date [#{sourceId}][Rev{sourceRev}]: {sourceClosedDate} ",
                        sourceWorkItem.ToWorkItem().Id, sourceWorkItem.ToWorkItem().Rev,
                        sourceWorkItem.ToWorkItem().Fields[closedDateField].Value);
                }
                else
                {
                    Log.LogWarning("Source Closed Date [#{sourceId}][Rev{sourceRev}] is not Available ",
                        sourceWorkItem.ToWorkItem().Id, sourceWorkItem.ToWorkItem().Rev);
                }
            }
            if (!sourceWorkItem.ToWorkItem().Fields.Contains(closedDateField))
            {
                Log.LogWarning("The ClosedDate field {closedDateField} on the Target does not exist in the source! You can fix this with a mapping!", closedDateField);
                if (sourceWorkItem.ToWorkItem().Fields.Contains("Microsoft.VSTS.Common.ClosedDate"))
                {
                    Log.LogWarning("Source ClosedDate Field: ", "Microsoft.VSTS.Common.ClosedDate");
                }
                if (sourceWorkItem.ToWorkItem().Fields.Contains("System.ClosedDate"))
                {
                    Log.LogWarning("Source ClosedDate Field: ", "System.ClosedDate");
                }
                if (targetWorkItem.ToWorkItem().Fields.Contains("Microsoft.VSTS.Common.ClosedDate"))
                {
                    Log.LogWarning("Target ClosedDate Field: ", "Microsoft.VSTS.Common.ClosedDate");
                }
                if (targetWorkItem.ToWorkItem().Fields.Contains("System.ClosedDate"))
                {
                    Log.LogWarning("Target ClosedDate Field: ", "System.ClosedDate");
                }
            }
        }

        private bool SkipRevisionWithInvalidIterationPath(WorkItemData targetWorkItemData)
        {
            if (!Options.SkipRevisionWithInvalidIterationPath)
            {
                return false;
            }

            return ValidateRevisionField(targetWorkItemData, "System.IterationPath");
        }

        private bool SkipRevisionWithInvalidAreaPath(WorkItemData targetWorkItemData)
        {
            if (!Options.SkipRevisionWithInvalidAreaPath)
            {
                return false;
            }

            return ValidateRevisionField(targetWorkItemData, "System.AreaPath"); ;
        }

        private bool ValidateRevisionField(WorkItemData targetWorkItemData, string fieldReferenceName)
        {
            var workItem = targetWorkItemData.ToWorkItem();
            var invalidFields = workItem.Validate();

            if (invalidFields.Count == 0)
            {
                return false;
            }

            foreach (Field invalidField in invalidFields)
            {
                // We cannot save a revision when it has no IterationPath and/or AreaPath
                if (invalidField.ReferenceName == fieldReferenceName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
