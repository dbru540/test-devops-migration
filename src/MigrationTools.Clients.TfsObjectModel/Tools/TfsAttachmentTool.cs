using System;
using System.IO;
using System.Linq;
using System.Collections.Generic; 
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Server;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Proxy;
using MigrationTools._EngineV1.Configuration.Processing;
using MigrationTools.DataContracts;
using MigrationTools.Endpoints;
using MigrationTools.Enrichers;
using MigrationTools.Processors;
using MigrationTools.Processors.Infrastructure;
using MigrationTools.Tools.Infrastructure;
using Serilog;

namespace MigrationTools.Tools
{
    /// <summary>
    /// Tool for processing and migrating work item attachments between Team Foundation Server instances, handling file downloads, uploads, and attachment metadata.
    /// </summary>
    public class TfsAttachmentTool : Tool<TfsAttachmentToolOptions>
    {
        private string _exportWiPath;
        private WorkItemServer _sourceWorkItemServer;
        private WorkItemServer _targetWorkItemServer;

        /// <summary>
        /// Initializes a new instance of the TfsAttachmentTool class.
        /// </summary>
        /// <param name="options">Configuration options for the attachment tool</param>
        /// <param name="services">Service provider for dependency injection</param>
        /// <param name="logger">Logger for the tool operations</param>
        /// <param name="telemetryLogger">Telemetry logger for tracking operations</param>
        public TfsAttachmentTool(IOptions<TfsAttachmentToolOptions> options, IServiceProvider services, ILogger<TfsAttachmentTool> logger, ITelemetryLogger telemetryLogger) : base(options, services, logger, telemetryLogger)
        {

        }

        /// <summary>
        /// Processes and migrates attachments from a source work item to a target work item.
        /// </summary>
        /// <param name="processor">The TFS processor performing the migration</param>
        /// <param name="source">The source work item containing attachments to migrate</param>
        /// <param name="target">The target work item to receive the attachments</param>
        /// <param name="save">Whether to save the target work item after processing attachments</param>
        // Modified ProcessAttachments method to pass all source attachments for counting
        // Update ProcessAttachments to pass all source attachments
        public void ProcessAttachments(TfsProcessor processor, WorkItemData source, WorkItemData target, bool save = true)
        {
            Log.LogWarning("=== ATTACHMENT SYNC v3.0 - DEDUPLICATE (KEEP 1 COPY PER UNIQUE FILE) ===");
            Log.LogInformation("Starting ProcessAttachments for Source WI: {SourceId} -> Target WI: {TargetId}", 
                source?.Id, target?.Id);   
            
            SetupWorkItemServers(processor);
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var sourceAttachments = source.ToWorkItem().Attachments;
            
            Log.LogInformation("Processing {AttachmentCount} attachments from {SourceWorkItemID} to {TargetWorkItemID}",
                sourceAttachments.Count, source.Id, target.Id);

            _exportWiPath = Path.Combine(Options.ExportBasePath, source.ToWorkItem().Id.ToString());
            if (Directory.Exists(_exportWiPath))
            {
                Directory.Delete(_exportWiPath, true);
            }
            Directory.CreateDirectory(_exportWiPath);

            int count = 0;
            int skipped = 0;
            int added = 0;

            // Process each attachment
            foreach (Attachment wia in sourceAttachments)
            {
                count++;

                try
                {
                    string filepath = null;
                    Directory.CreateDirectory(Path.Combine(_exportWiPath, wia.Id.ToString()));
                    filepath = ExportAttachment(wia, _exportWiPath);
                    Log.LogDebug("Exported {Filename} to disk", Path.GetFileName(filepath));

                    if (filepath != null)
                    {
                        // Pass all source attachments for counting
                        bool wasAdded = ImportAttachment(target.ToWorkItem(), wia, filepath, sourceAttachments);
                        if (wasAdded)
                        {
                            added++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Unable to process attachment from source wi {SourceWorkItemId} called {AttachmentName}",
                        source.ToWorkItem().Id, wia.Name);
                    Telemetry.TrackException(ex, null);
                }
            }

            if (save)
            {
                target.SaveToAzureDevOps();
                Log.LogInformation("Attachment processing complete. Processed: {Processed}, Added: {Added}, Skipped: {Skipped}, Target now has {AttachmentCount} attachments",
                    count, added, skipped, target.ToWorkItem().Attachments.Count);
                CleanUpAfterSave();
            }
        }

        /// <summary>
        /// Cleans the up after save.
        /// </summary>
        public void CleanUpAfterSave()
        {
            if (_exportWiPath != null && Directory.Exists(_exportWiPath))
            {
                try
                {
                    Directory.Delete(_exportWiPath, true);
                    _exportWiPath = null;
                }
                catch (Exception)
                {
                    Log.LogWarning("ERROR: Unable to delete folder {ExportPath}! Should be cleaned up at the end.", _exportWiPath);
                }
            }
        }

        /// <summary>
        /// Exports the attachment.
        /// </summary>
        /// <param name="wia">The wia.</param>
        /// <param name="exportpath">The exportpath.</param>
        /// <returns>A string.</returns>
        private string ExportAttachment(Attachment wia, string exportpath)
        {
            string fname = GetSafeFilename(wia.Name);
            Log.LogDebug("Processing attachment: {AttachmentName}", fname);

            string fpath = Path.Combine(exportpath, wia.Id.ToString(), fname);

            if (!File.Exists(fpath))
            {
                Log.LogDebug("...downloading {FileName} to {ExportPath}", fname, exportpath);
                try
                {
                    var fileLocation = _sourceWorkItemServer.DownloadFile(wia.Id);
                    File.Copy(fileLocation, fpath, true);
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Exception downloading attachment");
                    Telemetry.TrackException(ex, null);
                    return null;
                }
            }
            else
            {
                Log.LogDebug("...already downloaded");
            }
            return fpath;
        }

        /// <summary>
        /// Imports an attachment to the target work item, intelligently handling duplicates for bidirectional sync
        /// </summary>
        /// <returns>True if attachment was added, false if it was skipped</returns>
        /// <summary>
        /// Imports an attachment to the target work item, preserving the exact number of duplicates from source
        /// </summary>
        /// <returns>True if attachment was added, false if it was skipped</returns>
        private bool ImportAttachment(WorkItem targetWorkItem, Attachment sourceAttachment, string filepath, 
            AttachmentCollection allSourceAttachments)
        {
            var filename = Path.GetFileName(filepath);
            FileInfo fi = new FileInfo(filepath);

            if (Options.MaxAttachmentSize > fi.Length)
            {
                // Calculate source checksum once
                string sourceChecksum = CalculateFileChecksum(filepath);

                Log.LogDebug("Processing: {Name}, Length: {Length}, Checksum: {Checksum}",
                    sourceAttachment.Name, sourceAttachment.Length, sourceChecksum.Substring(0, 8) + "...");

                // DEDUPLICATION MODE: Check if target already has ANY copy with same checksum
                // No need to count source duplicates - we only keep 1 copy per unique file

                // Check if target already has this file (by name + length + checksum)
                bool targetHasCopy = false;
                var targetAttachments = targetWorkItem.Attachments.Cast<Attachment>().ToList();

                foreach (var tgtAtt in targetAttachments)
                {
                    // Skip unsaved attachments (ID = 0) - they were just added in this batch
                    if (tgtAtt.Id == 0)
                    {
                        // Check pending attachments by name/length (assume same content since we just added it)
                        if (tgtAtt.Name == sourceAttachment.Name && tgtAtt.Length == sourceAttachment.Length)
                        {
                            targetHasCopy = true;
                            Log.LogDebug("Found pending (unsaved) copy: {Name} (ID=0)", tgtAtt.Name);
                            break;
                        }
                        continue;
                    }

                    // Check name and length first
                    if (tgtAtt.Name == sourceAttachment.Name && tgtAtt.Length == sourceAttachment.Length)
                    {
                        // Download and check checksum
                        string targetChecksum = GetTargetAttachmentChecksum(tgtAtt);
                        if (targetChecksum != null && targetChecksum == sourceChecksum)
                        {
                            targetHasCopy = true;
                            Log.LogDebug("Found existing copy in target: {Name} (ID={Id})", tgtAtt.Name, tgtAtt.Id);
                            break; // Found one, no need to check more
                        }
                    }
                }

                Log.LogInformation("File '{Name}' (checksum {Checksum}): Target {HasCopy}",
                    sourceAttachment.Name, sourceChecksum.Substring(0, 8) + "...",
                    targetHasCopy ? "already has a copy" : "needs this file");

                // DECISION: Only add if target has NO copy of this file
                if (!targetHasCopy)
                {
                    Attachment a = new Attachment(filepath);
                    
                    // Always set the comment from source (clean it if needed)
                    string commentToUse = sourceAttachment.Comment ?? "";
                    if (commentToUse.Contains("[originalId:"))
                    {
                        commentToUse = CleanTechnicalTags(commentToUse);
                    }
                    a.Comment = commentToUse;
                    
                    Log.LogDebug("Adding attachment with comment: '{Comment}'", a.Comment);
                    
                    targetWorkItem.Attachments.Add(a);
                    Log.LogInformation("✓ Added attachment {FileName} to WorkItem {WorkItemId} with comment: '{Comment}'",
                        filename, targetWorkItem.Id, a.Comment);
                    return true;
                }
                else
                {
                    // Even when counts match, check if comments need to be synchronized
                    string expectedComment = sourceAttachment.Comment ?? "";
                    if (expectedComment.Contains("[originalId:"))
                    {
                        expectedComment = CleanTechnicalTags(expectedComment);
                    }
                    
                    // Check if we need to fix any comments
                    bool needsCommentFix = false;
                    List<Attachment> attachmentsToFix = new List<Attachment>();
                    
                    foreach (Attachment tgtAtt in targetWorkItem.Attachments)
                    {
                        // Skip unsaved attachments (ID = 0) - can't download or modify them
                        if (tgtAtt.Id == 0)
                        {
                            continue;
                        }

                        if (tgtAtt.Name == sourceAttachment.Name &&
                            tgtAtt.Length == sourceAttachment.Length)
                        {
                            string targetChecksum = GetTargetAttachmentChecksum(tgtAtt);

                            if (targetChecksum != null && targetChecksum == sourceChecksum)
                            {
                                if (tgtAtt.Comment != expectedComment)
                                {
                                    Log.LogWarning("⚠ Comment mismatch for {FileName}: Target='{TargetComment}', Source='{SourceComment}' - FIXING",
                                        filename, tgtAtt.Comment, expectedComment);

                                    attachmentsToFix.Add(tgtAtt);
                                    needsCommentFix = true;
                                    // Don't break - check all matching attachments
                                }
                                else
                                {
                                    Log.LogDebug("✓ Attachment {FileName} has correct comment: '{Comment}'",
                                        filename, expectedComment);
                                }
                            }
                        }
                    }
                    
                    // Only manipulate attachments if comments need fixing
                    if (needsCommentFix)
                    {
                        // Remove attachments with wrong comments
                        foreach (var tgtAtt in attachmentsToFix)
                        {
                            targetWorkItem.Attachments.Remove(tgtAtt);
                        }
                        
                        // Re-add with correct comment (only need to add once per unique file)
                        Attachment a = new Attachment(filepath);
                        a.Comment = expectedComment;
                        targetWorkItem.Attachments.Add(a);
                        
                        Log.LogInformation("✓ Fixed comment for attachment {FileName} to: '{Comment}'", 
                            filename, expectedComment);
                        
                        return true; // Return true since we made a change
                    }
                    else
                    {
                        // Everything is already in sync
                        Log.LogInformation("✓ [SYNCED] WorkItem {WorkItemId} already has {FileName} with correct comment: '{Comment}'",
                            targetWorkItem.Id, filename, expectedComment);
                        return false; // No changes needed
                    }
                }
            }
            else
            {
                Log.LogWarning("[SKIP] Attachment {FileName} exceeds size limit of {MaxAttachmentSize} bytes",
                    filename, Options.MaxAttachmentSize);
                return false;
            }
        }

        /// <summary>
        /// Calculates SHA256 checksum for a file
        /// </summary>
        private string CalculateFileChecksum(string filepath)
        {
            try
            {
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    using (var stream = File.OpenRead(filepath))
                    {
                        var hash = sha256.ComputeHash(stream);
                        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Failed to calculate checksum for file {FilePath}", filepath);
                throw;
            }
        }

        /// <summary>
        /// Gets checksum of target attachment by downloading and calculating SHA256
        /// </summary>
        private string GetTargetAttachmentChecksum(Attachment targetAttachment)
        {
            try
            {
                // Download from target server
                var fileLocation = _targetWorkItemServer.DownloadFile(targetAttachment.Id);
                string checksum = CalculateFileChecksum(fileLocation);

                // Try to clean up the downloaded file if it's in a temp location
                try
                {
                    if (fileLocation.Contains(Path.GetTempPath()))
                    {
                        File.Delete(fileLocation);
                    }
                }
                catch
                {
                    // Non-critical - temp files will be cleaned up eventually
                }

                return checksum;
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Could not calculate checksum for target attachment {AttachmentName} (ID: {AttachmentId})",
                    targetAttachment.Name, targetAttachment.Id);
                // In case of error, return null and treat as different
                // This ensures we don't skip attachments due to download failures
                return null;
            }
        }

        /// <summary>
        /// Removes technical tags from comment strings
        /// </summary>
        private string CleanTechnicalTags(string comment)
        {
            if (string.IsNullOrWhiteSpace(comment))
                return "";

            // Remove [originalId:XXX] tags
            string regexPatternOriginalId = @"\[originalId:\d+\]";
            string cleaned = Regex.Replace(comment, regexPatternOriginalId, "");

            // Also remove any other potential technical tags you might encounter
            // Add more patterns here if needed

            // Clean up extra spaces
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            return cleaned;
        }

        /// <summary>
        /// Gets a safe filename by replacing invalid characters
        /// </summary>
        public string GetSafeFilename(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return "_unnamed_";
            var safe = string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));
            safe = safe.Replace("..", "_");
            safe = Path.GetFileName(safe);
            return string.IsNullOrWhiteSpace(safe) ? "_unnamed_" : safe;
        }

        /// <summary>
        /// Sets up both source and target WorkItemServers for attachment operations
        /// </summary>
        private void SetupWorkItemServers(TfsProcessor processor)
        {
            if (_sourceWorkItemServer == null)
            {
                Log.LogInformation("Setting up WorkItemServers...");
                
                IMigrationEngine engine = Services.GetRequiredService<IMigrationEngine>();
                _sourceWorkItemServer = processor.Source.GetService<WorkItemServer>();
                _targetWorkItemServer = processor.Target.GetService<WorkItemServer>();

                Log.LogInformation("Source server: {Source}", _sourceWorkItemServer != null ? "OK" : "NULL");
                Log.LogInformation("Target server: {Target}", _targetWorkItemServer != null ? "OK" : "NULL");

                if (_sourceWorkItemServer == null)
                {
                    throw new InvalidOperationException("Could not get WorkItemServer from source");
                }

                if (_targetWorkItemServer == null)
                {
                    throw new InvalidOperationException("Could not get WorkItemServer from target");
                }
            }
        }
        
        
        /// <summary>
        /// Cleans up duplicate attachments in a work item, removing duplicates and originalId tags
        /// </summary>
        /// <param name="workItemData">The work item to clean</param>
        /// <param name="workItemServer">The server to download attachments from</param>
        /// <param name="isSource">True if this is source, false if target</param>
        /// <returns>Number of attachments removed</returns>
        private int CleanupDuplicateAttachments(WorkItemData workItemData, WorkItemServer workItemServer, bool isSource)
        {
            var workItem = workItemData.ToWorkItem();
            Log.LogWarning("=== CLEANUP: Starting deduplication for {WorkItemType} Work Item {Id} with {Count} attachments ===", 
                isSource ? "SOURCE" : "TARGET", workItem.Id, workItem.Attachments.Count);
            
            if (workItem.Attachments.Count == 0)
            {
                Log.LogInformation("No attachments to clean up");
                return 0;
            }
            
            // Add safety check for too many attachments
            if (workItem.Attachments.Count > 50)
            {
                Log.LogWarning("⚠️ Work item has {Count} attachments - this may take a while!", 
                    workItem.Attachments.Count);
            }
            
            // Create temp directory for this work item's cleanup
            string cleanupPath = Path.Combine(Options.ExportBasePath, "cleanup", workItem.Id.ToString());
            if (Directory.Exists(cleanupPath))
            {
                Directory.Delete(cleanupPath, true);
            }
            Directory.CreateDirectory(cleanupPath);
            
            try
            {
                // Step 1: First pass - identify attachments with [originalId:] quickly
                var attachmentsWithOriginalId = new List<Attachment>();
                var normalAttachments = new List<Attachment>();
                
                foreach (Attachment att in workItem.Attachments)
                {
                    if (att.Comment != null && att.Comment.Contains("[originalId:"))
                    {
                        attachmentsWithOriginalId.Add(att);
                        Log.LogDebug("Found attachment with originalId tag: {Name}", att.Name);
                    }
                    else
                    {
                        normalAttachments.Add(att);
                    }
                }
                
                Log.LogWarning("Found {WithId} attachments with [originalId:] tags and {Normal} normal attachments",
                    attachmentsWithOriginalId.Count, normalAttachments.Count);
                
                // Step 2: Process attachments in batches to avoid memory issues
                var attachmentInfo = new List<AttachmentInfo>();
                int batchSize = 20; // Process 20 at a time
                int processed = 0;
                
                var allAttachments = workItem.Attachments.Cast<Attachment>().ToList();
                
                for (int i = 0; i < allAttachments.Count; i += batchSize)
                {
                    var batch = allAttachments.Skip(i).Take(batchSize).ToList();
                    Log.LogInformation("Processing batch {Current}/{Total} ({Count} attachments)...", 
                        (i / batchSize) + 1, 
                        (allAttachments.Count + batchSize - 1) / batchSize,
                        batch.Count);
                    
                    foreach (var att in batch)
                    {
                        try
                        {
                            processed++;
                            Log.LogDebug("[{Current}/{Total}] Processing: {Name} (ID: {Id})", 
                                processed, allAttachments.Count, att.Name, att.Id);
                            
                            string attachmentDir = Path.Combine(cleanupPath, att.Id.ToString());
                            Directory.CreateDirectory(attachmentDir);
                            string attachmentPath = Path.Combine(attachmentDir, GetSafeFilename(att.Name));
                            
                            // Download with retry logic for large datasets
                            int retryCount = 3;
                            string checksum = null;
                            
                            for (int retry = 0; retry < retryCount; retry++)
                            {
                                try
                                {
                                    var fileLocation = workItemServer.DownloadFile(att.Id);
                                    File.Copy(fileLocation, attachmentPath, true);
                                    checksum = CalculateFileChecksum(attachmentPath);
                                    break;
                                }
                                catch (Exception ex) when (retry < retryCount - 1)
                                {
                                    Log.LogWarning("Retry {Retry}/{Max} for attachment {Id}: {Error}", 
                                        retry + 1, retryCount, att.Id, ex.Message);
                                    System.Threading.Thread.Sleep(1000 * (retry + 1)); // Exponential backoff
                                }
                            }
                            
                            if (checksum != null)
                            {
                                attachmentInfo.Add(new AttachmentInfo
                                {
                                    Attachment = att,
                                    Name = att.Name,
                                    Length = att.Length,
                                    Checksum = checksum,
                                    FilePath = attachmentPath,
                                    OriginalComment = att.Comment,
                                    CleanComment = CleanTechnicalTags(att.Comment),
                                    HasOriginalId = att.Comment != null && att.Comment.Contains("[originalId:")
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.LogError(ex, "Failed to process attachment {Name} (ID: {Id})", att.Name, att.Id);
                        }
                    }
                    
                    // Clean up temp files after each batch to save disk space
                    foreach (var info in attachmentInfo)
                    {
                        try
                        {
                            if (File.Exists(info.FilePath))
                            {
                                File.Delete(info.FilePath);
                            }
                        }
                        catch { /* Non-critical */ }
                    }
                }
                
                // Step 3: Identify unique attachments
                var uniqueGroups = attachmentInfo
                    .GroupBy(a => new { a.Name, a.Length, a.Checksum })
                    .ToList();
                
                Log.LogWarning("🔍 Found {UniqueCount} unique attachments out of {TotalCount} total attachments", 
                    uniqueGroups.Count, attachmentInfo.Count);
                
                // Step 4: Determine what to keep
                var attachmentsToRemove = new List<AttachmentInfo>();
                var attachmentsToKeep = new List<AttachmentInfo>();
                
                foreach (var group in uniqueGroups)
                {
                    var duplicates = group.ToList();
                    if (duplicates.Count > 1)
                    {
                        Log.LogWarning("Found {Count} duplicates of '{Name}' (checksum: {Checksum})", 
                            duplicates.Count, group.Key.Name, group.Key.Checksum.Substring(0, 8) + "...");
                        
                        // Keep the one with the cleanest comment
                        var keeper = duplicates
                            .OrderBy(a => a.HasOriginalId ? 1 : 0)
                            .ThenBy(a => string.IsNullOrWhiteSpace(a.CleanComment) ? 0 : 1)
                            .First();
                        
                        attachmentsToKeep.Add(keeper);
                        attachmentsToRemove.AddRange(duplicates.Where(a => a != keeper));
                    }
                    else
                    {
                        attachmentsToKeep.Add(duplicates[0]);
                    }
                }
                
                // Step 5: Apply changes if needed
                if (attachmentsToRemove.Count > 0 || attachmentsToKeep.Any(a => a.HasOriginalId))
                {
                    Log.LogWarning("🗑️ Removing {RemoveCount} duplicate attachments and cleaning {CleanCount} comments", 
                        attachmentsToRemove.Count, 
                        attachmentsToKeep.Count(a => a.HasOriginalId));
                    
                    // Clear all and rebuild with clean attachments
                    workItem.Attachments.Clear();
                    
                    // Re-add unique attachments with clean comments
                    // Re-download files for the ones we're keeping
                    foreach (var info in attachmentsToKeep)
                    {
                        try
                        {
                            string keepPath = Path.Combine(cleanupPath, $"keep_{info.Attachment.Id}", GetSafeFilename(info.Name));
                            Directory.CreateDirectory(Path.GetDirectoryName(keepPath));
                            
                            var fileLocation = workItemServer.DownloadFile(info.Attachment.Id);
                            File.Copy(fileLocation, keepPath, true);
                            
                            var newAttachment = new Attachment(keepPath);
                            newAttachment.Comment = info.CleanComment;
                            workItem.Attachments.Add(newAttachment);
                            
                            Log.LogDebug("✓ Re-added attachment: {Name} with clean comment", info.Name);
                        }
                        catch (Exception ex)
                        {
                            Log.LogError(ex, "Failed to re-add attachment {Name}", info.Name);
                        }
                    }
                    
                    // Save with retry logic for large operations
                    int saveRetries = 3;
                    for (int retry = 0; retry < saveRetries; retry++)
                    {
                        try
                        {
                            workItem.Save();
                            Log.LogWarning("✅ SUCCESS: Work item {Id} saved with {Count} unique attachments (removed {Removed} duplicates)", 
                                workItem.Id, attachmentsToKeep.Count, attachmentsToRemove.Count);
                            break;
                        }
                        catch (Exception ex) when (retry < saveRetries - 1)
                        {
                            Log.LogWarning("Save retry {Retry}/{Max}: {Error}", 
                                retry + 1, saveRetries, ex.Message);
                            System.Threading.Thread.Sleep(2000 * (retry + 1));
                        }
                    }
                    
                    return attachmentsToRemove.Count;
                }
                else
                {
                    Log.LogInformation("✅ No duplicates found and no comments need cleaning");
                    return 0;
                }
            }
            finally
            {
                // Cleanup temp files
                try
                {
                    Directory.Delete(cleanupPath, true);
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Could not delete cleanup directory: {Error}", ex.Message);
                }
            }
        }

        /// <summary>
        /// Helper class to store attachment information during cleanup
        /// </summary>
        private class AttachmentInfo
        {
            /// <summary>
            /// Gets or sets the attachment.
            /// </summary>
            public Attachment Attachment { get; set; }
            /// <summary>
            /// Gets or sets the name.
            /// </summary>
            public string Name { get; set; }
            /// <summary>
            /// Gets or sets the length.
            /// </summary>
            public long Length { get; set; }
            /// <summary>
            /// Gets or sets the checksum.
            /// </summary>
            public string Checksum { get; set; }
            /// <summary>
            /// Gets or sets the file path.
            /// </summary>
            public string FilePath { get; set; }
            /// <summary>
            /// Gets or sets the original comment.
            /// </summary>
            public string OriginalComment { get; set; }
            /// <summary>
            /// Gets or sets the clean comment.
            /// </summary>
            public string CleanComment { get; set; }
            /// <summary>
            /// Gets or sets a value indicating whether has original id.
            /// </summary>
            public bool HasOriginalId { get; set; }
        }

        /// <summary>
        /// Performs cleanup on both source and target, then syncs attachments
        /// </summary>
        public void CleanupAndProcessAttachments(TfsProcessor processor, WorkItemData source, WorkItemData target, bool save = true)
        {
            Log.LogWarning("=== CLEANUP AND SYNC PROCESS STARTING ===");
            Log.LogInformation("Processing Source WI: {SourceId} and Target WI: {TargetId}", source?.Id, target?.Id);
            
            SetupWorkItemServers(processor);
            
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            
            // Step 1: Clean source work item
            Log.LogWarning("STEP 1: Cleaning SOURCE work item {Id}", source.Id);
            int sourceRemoved = CleanupDuplicateAttachments(source, _sourceWorkItemServer, true);
            
            // Step 2: Clean target work item  
            Log.LogWarning("STEP 2: Cleaning TARGET work item {Id}", target.Id);
            int targetRemoved = CleanupDuplicateAttachments(target, _targetWorkItemServer, false);
            
            // Step 3: Reload work items after cleanup to get fresh attachment lists
            if (sourceRemoved > 0 || targetRemoved > 0)
            {
                Log.LogInformation("Reloading work items after cleanup...");
                
                // Store IDs
                string sourceId = source.Id;
                string targetId = target.Id;
                
                // Close current instances
                source.ToWorkItem().Close();
                target.ToWorkItem().Close();
                
                // Get fresh instances
                source = processor.Source.WorkItems.GetWorkItem(sourceId);
                target = processor.Target.WorkItems.GetWorkItem(targetId);
                
                // IMPORTANT: Open the work items before using them
                if (!source.ToWorkItem().IsOpen)
                {
                    source.ToWorkItem().Open();
                }
                
                if (!target.ToWorkItem().IsOpen)
                {
                    target.ToWorkItem().Open();
                }
                
                Log.LogInformation("Work items reloaded - Source has {SourceCount} attachments, Target has {TargetCount} attachments",
                    source.ToWorkItem().Attachments.Count, target.ToWorkItem().Attachments.Count);
                    
                // CRITICAL: After reloading, we need to re-fix embedded images!
                // Check if EmbededImages tool is available and enabled
                if (processor.CommonTools != null && processor.CommonTools.EmbededImages != null && processor.CommonTools.EmbededImages.Enabled)
                {
                    Log.LogError("⚠️ RE-FIXING embedded images after work item reload");
                    processor.CommonTools.EmbededImages.FixEmbededImages(processor, target);
                }                   
            }
            
            // Step 4: Now run the normal sync process with clean attachment lists
            Log.LogWarning("STEP 3: Running normal attachment sync process");
            ProcessAttachments(processor, source, target, save);
            
            Log.LogWarning("=== CLEANUP AND SYNC PROCESS COMPLETE ===");
            Log.LogInformation("Summary: Removed {SourceRemoved} duplicates from source, {TargetRemoved} from target", 
                sourceRemoved, targetRemoved);
        }

        /// <summary>
        /// Checks if a work item needs cleanup (has duplicates or originalId comments)
        /// </summary>
        public bool NeedsCleanup(WorkItemData workItemData)
        {
            var workItem = workItemData.ToWorkItem();
            
            if (workItem.Attachments.Count == 0)
                return false;
            
            // Check for originalId in comments
            bool hasOriginalId = false;
            var attachmentSignatures = new HashSet<string>();
            //bool hasDuplicates = false;
            
            foreach (Attachment att in workItem.Attachments)
            {
                // Check for originalId
                if (att.Comment != null && att.Comment.Contains("[originalId:"))
                {
                    hasOriginalId = true;
                    break; // No need to check further
                }
                
                // Check for duplicates by name and length
                //string signature = $"{att.Name}|{att.Length}";
                //if (!attachmentSignatures.Add(signature))
                //{
                //    hasDuplicates = true;
                //}
                //
                //if (hasOriginalId && hasDuplicates)
                //    break; // No need to check further
            }
            
            return hasOriginalId; //|| hasDuplicates
        }

        /// <summary>
        /// Main entry point - cleans up source duplicates first, then syncs to target
        /// </summary>
        public void SmartProcessAttachments(TfsProcessor processor, WorkItemData source, WorkItemData target, bool save = true)
        {
            Log.LogWarning("=== SMART ATTACHMENT SYNC v3.0 - CLEANUP SOURCE + DEDUPLICATE ===");

            SetupWorkItemServers(processor);

            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            // STEP 1: Check if source has duplicates that need cleanup
            int sourceDuplicateCount = CountDuplicates(source);

            if (sourceDuplicateCount > 0)
            {
                Log.LogWarning("🧹 SOURCE has {DuplicateCount} duplicate attachments - cleaning up first", sourceDuplicateCount);
                int sourceRemoved = CleanupDuplicateAttachments(source, _sourceWorkItemServer, true);

                if (sourceRemoved > 0)
                {
                    Log.LogInformation("Reloading source work item after cleanup...");

                    // Reload source to get fresh attachment list
                    string sourceId = source.Id;
                    source.ToWorkItem().Close();
                    source = processor.Source.WorkItems.GetWorkItem(sourceId);

                    if (!source.ToWorkItem().IsOpen)
                    {
                        source.ToWorkItem().Open();
                    }

                    Log.LogInformation("Source reloaded - now has {Count} attachments",
                        source.ToWorkItem().Attachments.Count);
                }
            }
            else
            {
                Log.LogInformation("✓ Source has no duplicates - skipping cleanup");
            }

            // STEP 2: Check if target has duplicates that need cleanup
            int targetDuplicateCount = CountDuplicates(target);

            if (targetDuplicateCount > 0)
            {
                Log.LogWarning("🧹 TARGET has {DuplicateCount} duplicate attachments - cleaning up", targetDuplicateCount);
                int targetRemoved = CleanupDuplicateAttachments(target, _targetWorkItemServer, false);

                if (targetRemoved > 0)
                {
                    Log.LogInformation("Reloading target work item after cleanup...");

                    // Reload target to get fresh attachment list
                    string targetId = target.Id;
                    target.ToWorkItem().Close();
                    target = processor.Target.WorkItems.GetWorkItem(targetId);

                    if (!target.ToWorkItem().IsOpen)
                    {
                        target.ToWorkItem().Open();
                    }

                    Log.LogInformation("Target reloaded - now has {Count} attachments",
                        target.ToWorkItem().Attachments.Count);

                    // Re-fix embedded images if needed
                    if (processor.CommonTools != null && processor.CommonTools.EmbededImages != null && processor.CommonTools.EmbededImages.Enabled)
                    {
                        Log.LogInformation("Re-fixing embedded images after target cleanup");
                        processor.CommonTools.EmbededImages.FixEmbededImages(processor, target);
                    }
                }
            }
            else
            {
                Log.LogInformation("✓ Target has no duplicates - skipping cleanup");
            }

            // STEP 3: Now run the normal sync process with clean attachment lists
            Log.LogWarning("STEP 3: Running attachment sync (deduplicate mode)");
            ProcessAttachments(processor, source, target, save);

            Log.LogWarning("=== SMART ATTACHMENT SYNC COMPLETE ===");
        }

        /// <summary>
        /// Counts how many duplicate attachments exist in a work item (same name + length + checksum)
        /// </summary>
        private int CountDuplicates(WorkItemData workItemData)
        {
            var workItem = workItemData.ToWorkItem();

            if (workItem.Attachments.Count <= 1)
                return 0;

            // Quick check by name + length only (without downloading)
            var signatures = new Dictionary<string, int>();

            foreach (Attachment att in workItem.Attachments)
            {
                string signature = $"{att.Name}|{att.Length}";
                if (signatures.ContainsKey(signature))
                {
                    signatures[signature]++;
                }
                else
                {
                    signatures[signature] = 1;
                }
            }

            // Count duplicates (attachments beyond the first one with same signature)
            int duplicateCount = signatures.Values.Where(c => c > 1).Sum(c => c - 1);

            return duplicateCount;
        }        
        
        
        
        
        
    }


}
