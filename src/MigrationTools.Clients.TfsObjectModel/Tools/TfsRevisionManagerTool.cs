using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using MigrationTools.Clients;
using MigrationTools.DataContracts;
using MigrationTools.Endpoints;
using MigrationTools.Enrichers;
using MigrationTools.Processors;
using MigrationTools.Processors.Infrastructure;
using MigrationTools.Tools.Infrastructure;
using Newtonsoft.Json;

namespace MigrationTools.Tools
{

    /// <summary>
    /// The TfsRevisionManagerTool manipulates the revisions of a work item to reduce the number of revisions that are migrated.
    /// </summary>
    public class TfsRevisionManagerTool : Tool<TfsRevisionManagerToolOptions>
    {
        private const string SyncedCommentMarker = "<!-- SYNCED -->";
        private const string BackfilledCommentMarker = "<!-- BACKFILLED -->";
        private static readonly Regex LegacySyncedCommentPrefixRegex = new Regex(
            @"^\s*<b>\[Synced - Comment by .*?\]</b><br\/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BackfilledCommentPrefixRegex = new Regex(
            @"^\s*<b>\[BACKFILLED COMMENT - Source rev .*?\]</b><br\/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);


        public bool ReplayRevisions => Options.ReplayRevisions;

        public TfsRevisionManagerTool(IOptions<TfsRevisionManagerToolOptions> options, IServiceProvider services, ILogger<TfsRevisionManagerTool> logger, ITelemetryLogger telemetryLogger)
            : base(options, services, logger, telemetryLogger)
        {

        }


        public  void ProcessorExecutionBegin(TfsProcessor processor) // Could be a IProcessorEnricher
        {
            if (Options.Enabled)
            {
                Log.LogInformation("Filter Revisions.");


                RefreshForProcessorType(processor);
            }
        }

        protected  void RefreshForProcessorType(TfsProcessor processor)
        {
            if (processor == null)
            {
                throw new ArgumentNullException(nameof(processor));
            }
            ((TfsWorkItemMigrationClient)processor.Target.WorkItems).Store?.RefreshCache(true);

        }

        public List<RevisionItem> GetRevisionsToMigrate(List<RevisionItem> sourceRevisions, List<RevisionItem> targetRevisions)
        {
            EnforceDatesMustBeIncreasing(sourceRevisions);

            LogDebugCurrentSortedRevisions(sourceRevisions, "Source");
            LogDebugCurrentSortedRevisions(targetRevisions, "Target");
            Log.LogDebug("TfsRevisionManagerTool::GetRevisionsToMigrate: Raw {sourceWorkItem} Has {sortedRevisions} revisions", "Source", sourceRevisions.Count);

            sourceRevisions = RemoveRevisionsAlreadyOnTarget(targetRevisions, sourceRevisions);

            RemoveRevisionsAllExceptLatest(sourceRevisions);

            RemoveRevisionsMoreThanMaxRevisions(sourceRevisions);

            LogDebugCurrentSortedRevisions(sourceRevisions);

            return sourceRevisions;
        }

        public void EnforceDatesMustBeIncreasing(List<RevisionItem> sortedRevisions)
        {
            Log.LogDebug("TfsRevisionManagerTool::EnforceDatesMustBeIncreasing");
            DateTime lastDateTime = DateTime.MinValue;
            foreach (var revision in sortedRevisions)
            {
                if (revision.ChangedDate == lastDateTime || revision.OriginalChangedDate < lastDateTime)
                {
                    revision.ChangedDate = lastDateTime.AddSeconds(1);
                    Log.LogDebug("TfsRevisionManagerTool::EnforceDatesMustBeIncreasing[{revision}]::Fix", revision.Number);
                }
                lastDateTime = revision.ChangedDate;
            }
        }

        public void LogDebugCurrentSortedRevisions(List<RevisionItem> sortedRevisions, string designation = "Source")
        {
            if (sortedRevisions == null)
            {
                Log.LogDebug("{designation}: RevisionsToMigrate: No revisions to migrate", designation);
                return;
            }
            Log.LogInformation("{designation}: Found {RevisionsCount} revisions to migrate on  Work item:{sourceWorkItemId}", designation, sortedRevisions.Count, designation);
            Log.LogDebug("{designation}: RevisionsToMigrate:----------------------------------------------------", designation);
            foreach (RevisionItem item in sortedRevisions)
            {
                Log.LogDebug("RevisionsToMigrate: Index:{Index} - Number:{Number} - ChangedDate:{ChangedDate}", item.Index, item.Number, item.ChangedDate);
            }
            Log.LogDebug("{designation}: RevisionsToMigrate:----------------------------------------------------", designation);
        }

        private void RemoveRevisionsMoreThanMaxRevisions(List<RevisionItem> sortedRevisions)
        {
            if (Options.ReplayRevisions &&
                Options.MaxRevisions > 0 &&
                sortedRevisions.Count > 0 &&
                Options.MaxRevisions < sortedRevisions.Count)
            {
                var revisionsToRemove = sortedRevisions.Count - Options.MaxRevisions;
                sortedRevisions.RemoveRange(0, revisionsToRemove);
                Log.LogDebug("TfsRevisionManagerTool::GetRevisionsToMigrate::RemoveRevisionsMoreThanMaxRevisions MaxRevisions={MaxRevisions}! There are {sortedRevisionsCount} left", Options.MaxRevisions, sortedRevisions.Count);
            }
        }

        private void RemoveRevisionsAllExceptLatest(List<RevisionItem> sortedRevisions)
        {
            if (!Options.ReplayRevisions && sortedRevisions.Count > 0)
            {
                // Remove all but the latest revision if we are not replaying revisions
                sortedRevisions.RemoveRange(0, sortedRevisions.Count - 1);
                Log.LogDebug("TfsRevisionManagerTool::GetRevisionsToMigrate::RemoveRevisionsAllExceptLatest ReplayRevisions=false! There are {sortedRevisionsCount} left", sortedRevisions.Count);
            }
        }

        private List<RevisionItem> RemoveRevisionsAlreadyOnTarget(List<RevisionItem> targetRevisions, List<RevisionItem> sourceRevisions)
        {
            if (targetRevisions != null)
            {
                Log.LogDebug("TfsRevisionManagerTool::GetRevisionsToMigrate::RemoveRevisionsAlreadyOnTarget Raw Target Has {targetWorkItemRevCount} revisions", targetRevisions.Count);
                // Target exists so remove any Changed Date matches between them
                var targetChangedDates = (from RevisionItem x in targetRevisions select x.ChangedDate).ToList();
                if (Options.ReplayRevisions)
                {
                    sourceRevisions = sourceRevisions.Where(x => !targetChangedDates.Contains(x.ChangedDate)).ToList();
                    Log.LogDebug("TfsRevisionManagerTool::GetRevisionsToMigrate::RemoveRevisionsAlreadyOnTarget After removing Date Matches there are {sortedRevisionsCount} left", sourceRevisions.Count);
                }
                // Remove source revisions older than target latest date,
                // BUT keep any that carry System.History (comments) so missed comments can still sync.
                var targetLatestDate = targetChangedDates.Max();
                var olderRevisions = sourceRevisions.Where(x => x.ChangedDate <= targetLatestDate).ToList();
                var newerRevisions = sourceRevisions.Where(x => x.ChangedDate > targetLatestDate).ToList();
                var targetHistoryValues = targetRevisions
                    .Where(x => x.Fields != null && x.Fields.ContainsKey("System.History"))
                    .Select(x => NormalizeHistoryValue(x.Fields["System.History"].Value?.ToString()))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToHashSet(StringComparer.Ordinal);
                var missedComments = olderRevisions.Where(x =>
                {
                    if (x.Fields == null || !x.Fields.ContainsKey("System.History"))
                    {
                        return false;
                    }

                    var historyValue = NormalizeHistoryValue(x.Fields["System.History"].Value?.ToString());
                    return !string.IsNullOrWhiteSpace(historyValue) && !targetHistoryValues.Contains(historyValue);
                }
                ).ToList();
                if (missedComments.Count > 0)
                {
                    Log.LogInformation("TfsRevisionManagerTool::RemoveRevisionsAlreadyOnTarget Found {missedCount} older revision(s) with System.History that were not yet on target - keeping them for catch-up", missedComments.Count);
                }
                sourceRevisions = missedComments.Concat(newerRevisions).OrderBy(x => x.ChangedDate).ToList();
                Log.LogDebug("TfsRevisionManagerTool::GetRevisionsToMigrate::RemoveRevisionsAlreadyOnTarget After filtering with comment catch-up there are {sortedRevisionsCount} left", sourceRevisions.Count);
            }
            else
            {
                Log.LogDebug("TfsRevisionManagerTool::GetRevisionsToMigrate::RemoveRevisionsAlreadyOnTarget Target is null");
            }
            return sourceRevisions;
        }

        private static string NormalizeHistoryValue(string historyValue)
        {
            if (string.IsNullOrWhiteSpace(historyValue))
            {
                return string.Empty;
            }

            string normalized = historyValue
                .Replace(SyncedCommentMarker, string.Empty)
                .Replace(BackfilledCommentMarker, string.Empty)
                .Trim();
            normalized = LegacySyncedCommentPrefixRegex.Replace(normalized, string.Empty).Trim();
            normalized = BackfilledCommentPrefixRegex.Replace(normalized, string.Empty).Trim();
            return normalized;
        }

        public void AttachSourceRevisionHistoryJsonToTarget(WorkItemData sourceWorkItem, WorkItemData targetWorkItem)
        {
            var fileData = JsonConvert.SerializeObject(sourceWorkItem.Revisions, new JsonSerializerSettings { PreserveReferencesHandling = PreserveReferencesHandling.None });
            var filePath = Path.Combine(Path.GetTempPath(), $"{sourceWorkItem.ProjectName}-ID{sourceWorkItem.Id}-R{sourceWorkItem.Rev}-PreMigrationHistory.json");

            // todo: Delete this file after (!) WorkItem has been saved
            File.WriteAllText(filePath, fileData);

            if (targetWorkItem.internalObject != null)
            {
                targetWorkItem.ToWorkItem().Attachments.Add(new Attachment(filePath, "History has been consolidated into the attached file."));
            }

            Log.LogInformation("Attached a consolidated set of {RevisionCount} revisions.",
                new Dictionary<string, object>() {
                    {"RevisionCount", sourceWorkItem.Revisions.Count() }
                });
        }
    }
}
