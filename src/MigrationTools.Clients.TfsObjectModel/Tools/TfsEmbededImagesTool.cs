using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using MigrationTools.DataContracts;
using MigrationTools.Endpoints;
using MigrationTools.Options;
using MigrationTools.Processors.Infrastructure;
using MigrationTools.Tools.Infrastructure;

namespace MigrationTools.Tools
{
    public class TfsEmbededImagesTool : EmbededImagesRepairToolBase<TfsEmbededImagesToolOptions>
    {
        private const string RegexPatternForImageUrl = "(?<=<img.*?src=\")[^\"]*";
        private const string RegexPatternForImageFileName = "(?<=FileName=)[^=]*";
        private const string TargetDummyWorkItemTitle = "***** DELETE THIS - Migration Tool Generated Dummy Work Item For TfsEmbededImagesTool *****";

        private Project _targetProject;

        private readonly IDictionary<string, string> _cachedUploadedUrisBySourceValue;
        private readonly List<int> _allDummyWorkItemIds = new List<int>();

        private WorkItem _targetDummyWorkItem;

        public TfsEmbededImagesTool(IOptions<TfsEmbededImagesToolOptions> options, IServiceProvider services, ILogger<TfsEmbededImagesTool> logger, ITelemetryLogger telemetryLogger) : base(options, services, logger, telemetryLogger)
        {
            _cachedUploadedUrisBySourceValue = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        }

        public int FixEmbededImages(TfsProcessor processor, WorkItemData targetWorkItem)
        {
            _processor = processor;
            _targetProject = processor.Target.WorkItems.Project.ToProject();
            
            string? accessToken = processor.Source.Options.Authentication.AuthenticationMode switch
            {
                AuthenticationMode.AccessToken => processor.Source.Options.Authentication.AccessToken,
                AuthenticationMode.Windows => GetWindowsAuthToken(processor.Source.Options.Authentication.NetworkCredentials),
                _ => null
            };
            
            // Call the protected override method
            FixEmbededImages(targetWorkItem, 
                processor.Source.Options.Collection.AbsoluteUri, 
                processor.Target.Options.Collection.AbsoluteUri, 
                accessToken);
            
            // DON'T SAVE HERE - Let the processor handle saving
            // The work item will be saved later in the attachment processing
            var workItem = targetWorkItem.ToWorkItem();
            if (workItem.IsDirty)
            {
                Log.LogInformation("EMBEDDED IMAGES MODIFIED - Work item {Id} has pending changes that will be saved during attachment processing",
                    targetWorkItem.Id);
                return 1;
            }
            
            return 0;
        }

        private string GetWindowsAuthToken(NetworkCredentials cred)
            => Convert.ToBase64String(Encoding.ASCII.GetBytes($"{cred.Domain}\\{cred.UserName}:{cred.Password}"));

        public void ProcessorExecutionEnd(TfsProcessor processor)
        {
            if (processor != null) _processor = processor;
            if (_targetDummyWorkItem != null)
            {
                _targetDummyWorkItem.Close();
            }
            // Delete all dummy work items created during this run via REST API.
            // REST DELETE (soft delete to recycle bin) requires lower permissions
            // than the SOAP DestroyWorkItems (permanent delete).
            if (_allDummyWorkItemIds.Count > 0 && _processor != null)
            {
                string baseUri = _processor.Target.Options.Collection.AbsoluteUri.TrimEnd('/');
                string project = Uri.EscapeDataString(_processor.Target.Options.Project);
                string token = _processor.Target.Options.Authentication.AccessToken;

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Basic",
                            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));

                    foreach (int dummyId in _allDummyWorkItemIds)
                    {
                        try
                        {
                            string url = $"{baseUri}/{project}/_apis/wit/workItems/{dummyId}?api-version=7.1";
                            var response = client.DeleteAsync(url).Result;
                            if (response.IsSuccessStatusCode)
                            {
                                Log.LogInformation("Deleted dummy work item {DummyId} via REST API", dummyId);
                            }
                            else
                            {
                                Log.LogWarning("Failed to delete dummy work item {DummyId}: HTTP {StatusCode}",
                                    dummyId, (int)response.StatusCode);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning("Error deleting dummy work item {DummyId}: {Error}",
                                dummyId, ex.Message);
                        }
                    }
                }
                _allDummyWorkItemIds.Clear();
            }
        }

        /**
         *  from https://gist.github.com/pietergheysens/792ed505f09557e77ddfc1b83531e4fb
        */

        protected override void FixEmbededImages(WorkItemData wi, string oldTfsurl, string newTfsurl, string sourcePersonalAccessToken = "")
        {
            Log.LogInformation("EmbededImagesRepairEnricher: Fixing HTML field attachments for work item {Id} from {OldTfsurl} to {NewTfsUrl}", 
                wi.Id, oldTfsurl, newTfsurl);

            // Extract the target organization - ALL images must be in this org
            string targetOrg = ExtractOrganization(newTfsurl);
            Log.LogInformation("Target organization where ALL images should be hosted: {TargetOrg}", targetOrg);

            var workItem = wi.ToWorkItem();
            bool anyChanges = false;

            // Check ALL fields including System.History (comments)
            foreach (Field field in workItem.Fields)
            {
                // Include all HTML fields AND specifically System.History
                bool shouldProcess = false;
                
                if (field.FieldDefinition.FieldType == FieldType.Html)
                {
                    shouldProcess = true;
                }
                else if (field.ReferenceName == "System.History")
                {
                    // Comments/History field
                    shouldProcess = true;
                    Log.LogWarning("Checking System.History (Comments) field for embedded images");
                }
                else if (field.FieldDefinition.FieldType == FieldType.History)
                {
                    shouldProcess = true;
                }
                
                if (!shouldProcess)
                    continue;

                try
                {
                    string originalValue = (string)field.Value;
                    if (string.IsNullOrEmpty(originalValue))
                        continue;

                    // LOG THE ACTUAL CONTENT TO SEE WHAT WE'RE DEALING WITH
                    if (originalValue.Contains("dev.azure.com"))
                    {
                        Log.LogWarning("Field {FieldName} ({RefName}) contains dev.azure.com URLs", 
                            field.Name, field.ReferenceName);
                        
                        // Log a sample of the content
                        var sample = originalValue.Length > 500 ? originalValue.Substring(0, 500) : originalValue;
                        Log.LogDebug("Field content sample: {Sample}", sample);
                    }

                    string modifiedValue = originalValue;
                    
                    // Try a more aggressive pattern to find ALL Azure DevOps URLs
                    string pattern = @"https://dev\.azure\.com/[^/]+/[^""'\s<>]+";
                    MatchCollection matches = Regex.Matches(originalValue, pattern);
                    
                    Log.LogWarning("Found {Count} Azure DevOps URLs in field {FieldName} ({RefName})", 
                        matches.Count, field.Name, field.ReferenceName);
                    
                    foreach (Match match in matches)
                    {
                        string imageUrl = match.Value;
                        
                        // Clean up any HTML encoded characters
                        imageUrl = System.Net.WebUtility.HtmlDecode(imageUrl);
                        
                        Log.LogWarning("Found URL: {Url}", imageUrl);
                        
                        // Extract organization
                        string imageOrg = ExtractOrganization(imageUrl);
                        Log.LogWarning("URL organization: {Org}, Target organization: {Target}", imageOrg, targetOrg);
                        
                        // Check if it's an attachment URL and from wrong org
                        if (imageUrl.Contains("/_apis/wit/attachments/") && 
                            !imageOrg.Equals(targetOrg, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.LogWarning("WRONG ORG ATTACHMENT: {Url} is from {WrongOrg} but should be {CorrectOrg}",
                                imageUrl, imageOrg, targetOrg);
                            
                            // Force replacement
                            string cacheKey = $"{imageUrl}→{targetOrg}";
                            string newImageLink = "";
                            
                            if (_cachedUploadedUrisBySourceValue.ContainsKey(cacheKey))
                            {
                                newImageLink = _cachedUploadedUrisBySourceValue[cacheKey];
                            }
                            else
                            {
                                try
                                {
                                    string downloadToken = DetermineAccessToken(imageUrl, sourcePersonalAccessToken);
                                    newImageLink = UploadedAndRetrieveAttachmentLinkUrl(imageUrl, field.Name, wi, downloadToken);
                                    
                                    if (!string.IsNullOrWhiteSpace(newImageLink))
                                    {
                                        _cachedUploadedUrisBySourceValue[cacheKey] = newImageLink;
                                        Log.LogInformation("Uploaded embedded image: New URL is {NewUrl}", newImageLink);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.LogError(ex, "Failed to reupload image");
                                    if (_ignore404Errors) continue;
                                    throw;
                                }
                            }
                            
                            if (!string.IsNullOrWhiteSpace(newImageLink))
                            {
                                // Replace both the original URL and any HTML-encoded version
                                modifiedValue = modifiedValue.Replace(match.Value, newImageLink);
                                modifiedValue = modifiedValue.Replace(System.Net.WebUtility.HtmlEncode(match.Value), newImageLink);
                                anyChanges = true;
                                Log.LogInformation("Replaced embedded image URL in content: {Old} -> {New}", imageUrl, newImageLink);
                            }
                        }
                        else if (!imageOrg.Equals(targetOrg, StringComparison.OrdinalIgnoreCase))
                        {
                            // Not an attachment but still wrong org
                            Log.LogWarning("Found non-attachment URL from wrong org: {Url}", imageUrl);
                        }
                    }
                    
                    // Update field if changed
                    if (modifiedValue != originalValue)
                    {
                        field.Value = modifiedValue;
                        Log.LogInformation("Field {FieldName} ({RefName}) updated with new URLs",
                            field.Name, field.ReferenceName);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogError(ex, "Error processing field {FieldName} ({RefName})", 
                        field.Name, field.ReferenceName);
                }
            }
            
            // ALSO check the work item revisions/comments history
            if (workItem.Revisions.Count > 0)
            {
                Log.LogWarning("Checking {Count} revisions for embedded images in comments", workItem.Revisions.Count);
                
                // Get the latest revision (current state)
                var latestRevision = workItem.Revisions[workItem.Revisions.Count - 1];
                if (latestRevision.Fields.Contains("System.History"))
                {
                    string historyValue = (string)latestRevision.Fields["System.History"].Value;
                    if (!string.IsNullOrEmpty(historyValue) && historyValue.Contains("dev.azure.com"))
                    {
                        Log.LogWarning("Found Azure DevOps URL in revision history/comments that may need attention");
                        // Note: History field is read-only in revisions, we need to add a new comment to fix it
                    }
                }
            }
            
            if (anyChanges)
            {
                Log.LogInformation("Embedded images changes made - Work item {Id} needs to be saved", wi.Id);
            }
            else
            {
                Log.LogDebug("No embedded image changes needed - All images already correct or no images found");
            }
        }

        private bool IsFromWrongOrganization(string imageUrl, string expectedOrgUrl)
        {
            try
            {
                // Extract organization from URLs
                string imageOrg = ExtractOrganization(imageUrl);
                string expectedOrg = ExtractOrganization(expectedOrgUrl);
                
                if (string.IsNullOrEmpty(imageOrg) || string.IsNullOrEmpty(expectedOrg))
                    return false;
                
                // If the organizations don't match, the URL needs to be fixed
                bool isWrong = !imageOrg.Equals(expectedOrg, StringComparison.OrdinalIgnoreCase);
                
                if (isWrong)
                {
                    Log.LogWarning("Image organization mismatch. Image is from '{ImageOrg}' but should be '{ExpectedOrg}'", 
                        imageOrg, expectedOrg);
                }
                
                return isWrong;
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Error checking organization for URL: {Url}", imageUrl);
                return false;
            }
        }

        private string UploadedAndRetrieveAttachmentLinkUrl(string matchedSourceUri, string sourceFieldName, WorkItemData targetWorkItem, string sourcePersonalAccessToken)
        {
            Match newFileNameMatch = Regex.Match(matchedSourceUri, RegexPatternForImageFileName, RegexOptions.IgnoreCase);
            if (!newFileNameMatch.Success) return null;

            Log.LogDebug("EmbededImagesRepairEnricher: field '{fieldName}' has match: {matchValue}", sourceFieldName, WebUtility.HtmlDecode(matchedSourceUri));
            string fullImageFilePath = Path.GetTempPath() + newFileNameMatch.Value;

            try
            {
                // Create a handler that allows redirects
                var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    UseDefaultCredentials = false
                };

                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(2);
                    
                    // Determine the correct authentication based on the URL being accessed
                    string accessToken = DetermineAccessToken(matchedSourceUri, sourcePersonalAccessToken);
                    
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        // Check if it's already in Basic format or needs encoding
                        if (accessToken.StartsWith("Basic "))
                        {
                            httpClient.DefaultRequestHeaders.Authorization = 
                                new AuthenticationHeaderValue("Basic", accessToken.Substring(6));
                        }
                        else if (accessToken.Contains(":"))
                        {
                            // Already in user:password or :pat format
                            var encodedPat = Convert.ToBase64String(Encoding.ASCII.GetBytes(accessToken));
                            httpClient.DefaultRequestHeaders.Authorization = 
                                new AuthenticationHeaderValue("Basic", encodedPat);
                        }
                        else
                        {
                            // Just a PAT token
                            var encodedPat = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{accessToken}"));
                            httpClient.DefaultRequestHeaders.Authorization = 
                                new AuthenticationHeaderValue("Basic", encodedPat);
                        }
                    }
                    else
                    {
                        Log.LogWarning("No authentication token found for URL: {Url}", matchedSourceUri);
                    }
                    
                    // Add Azure DevOps specific headers
                    httpClient.DefaultRequestHeaders.Add("X-TFS-FedAuthRedirect", "Suppress");
                    
                    var result = DownloadFile(httpClient, matchedSourceUri, fullImageFilePath);
                    
                    if (!result.IsSuccessStatusCode)
                    {
                        if (_ignore404Errors && result.StatusCode == HttpStatusCode.NotFound)
                        {
                            Log.LogDebug("Image not found (404): {Uri}", matchedSourceUri);
                            return null;
                        }
                        else if (result.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            // More detailed error logging
                            LogAuthenticationError(matchedSourceUri, accessToken);
                            
                            // Option to skip on auth errors if configured
                            if (_ignore404Errors) // You might want a separate flag for auth errors
                            {
                                Log.LogWarning("Skipping image due to authentication error: {Uri}", matchedSourceUri);
                                return null;
                            }
                            
                            result.EnsureSuccessStatusCode();
                        }
                        else
                        {
                            Log.LogError("Download failed: {StatusCode} - {ReasonPhrase} from {Uri}", 
                                result.StatusCode, result.ReasonPhrase, matchedSourceUri);
                            result.EnsureSuccessStatusCode();
                        }
                    }
                }

                // Verify the downloaded file
                if (!File.Exists(fullImageFilePath) || new FileInfo(fullImageFilePath).Length == 0)
                {
                    Log.LogError("Downloaded file is empty or doesn't exist: {FilePath}", fullImageFilePath);
                    return null;
                }

                var imageBytes = File.ReadAllBytes(fullImageFilePath);
                if (GetImageFormat(imageBytes) == ImageFormat.unknown)
                {
                    // Log first few bytes to debug
                    var firstBytes = imageBytes.Take(100).ToArray();
                    var content = Encoding.UTF8.GetString(firstBytes);
                    Log.LogError("Not an image. First 100 bytes: {Content}", content);
                    
                    throw new Exception($"Downloaded content is not a valid image. Might be an auth page.");
                }

                var attachRef = UploadImageToTarget(targetWorkItem.ToWorkItem(), fullImageFilePath);
                if (attachRef == null)
                {
                    throw new Exception($"Unable to upload the image [{fullImageFilePath}].");
                }

                return attachRef.Url;
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Failed to process embedded image from {Uri}", matchedSourceUri);
                throw;
            }
            finally
            {
                if (File.Exists(fullImageFilePath))
                {
                    try { File.Delete(fullImageFilePath); } catch { }
                }
            }
        }

        private string DetermineAccessToken(string imageUrl, string providedToken)
        {
            // First, try the provided token (if any)
            if (!string.IsNullOrEmpty(providedToken))
            {
                Log.LogDebug("Using provided token for URL: {Url}", imageUrl);
                return providedToken;
            }
            
            // Parse the URL to determine which organization it belongs to
            Uri uri = new Uri(imageUrl);
            string host = uri.Host.ToLower();
            string pathOrg = "";
            
            // Extract organization from URL
            if (host.Contains("dev.azure.com"))
            {
                // Format: https://dev.azure.com/{organization}/
                var segments = uri.Segments;
                if (segments.Length > 1)
                {
                    pathOrg = segments[1].Trim('/').ToLower();
                }
            }
            else if (host.Contains("visualstudio.com"))
            {
                // Format: https://{organization}.visualstudio.com/
                pathOrg = host.Split('.')[0].ToLower();
            }
            
            Log.LogDebug("Image URL organization: {Org} from {Url}", pathOrg, imageUrl);
            
            // Check if it matches source organization
            string sourceOrg = ExtractOrganization(_processor.Source.Options.Collection.ToString());
            string targetOrg = ExtractOrganization(_processor.Target.Options.Collection.ToString());
            
            Log.LogDebug("Source Org: {SourceOrg}, Target Org: {TargetOrg}, Image Org: {ImageOrg}", 
                sourceOrg, targetOrg, pathOrg);
            
            if (!string.IsNullOrEmpty(pathOrg))
            {
                if (pathOrg.Equals(sourceOrg, StringComparison.OrdinalIgnoreCase))
                {
                    // Use source authentication
                    if (_processor.Source.Options.Authentication.AuthenticationMode == AuthenticationMode.AccessToken)
                    {
                        Log.LogDebug("Using SOURCE token for organization: {Org}", pathOrg);
                        return _processor.Source.Options.Authentication.AccessToken;
                    }
                    else if (_processor.Source.Options.Authentication.AuthenticationMode == AuthenticationMode.Windows)
                    {
                        var creds = _processor.Source.Options.Authentication.NetworkCredentials;
                        return $"{creds.Domain}\\{creds.UserName}:{creds.Password}";
                    }
                }
                else if (pathOrg.Equals(targetOrg, StringComparison.OrdinalIgnoreCase))
                {
                    // Use target authentication
                    if (_processor.Target.Options.Authentication.AuthenticationMode == AuthenticationMode.AccessToken)
                    {
                        Log.LogDebug("Using TARGET token for organization: {Org}", pathOrg);
                        return _processor.Target.Options.Authentication.AccessToken;
                    }
                    else if (_processor.Target.Options.Authentication.AuthenticationMode == AuthenticationMode.Windows)
                    {
                        var creds = _processor.Target.Options.Authentication.NetworkCredentials;
                        return $"{creds.Domain}\\{creds.UserName}:{creds.Password}";
                    }
                }
            }
            
            // Fallback: try to guess based on current sync direction
            // If we're processing a work item from source to target, images are likely from source
            Log.LogWarning("Could not determine organization for URL: {Url}. Trying source token.", imageUrl);
            
            if (_processor.Source.Options.Authentication.AuthenticationMode == AuthenticationMode.AccessToken)
            {
                return _processor.Source.Options.Authentication.AccessToken;
            }
            
            return null;
        }

        private string ExtractOrganization(string collectionUrl)
        {
            Uri uri = new Uri(collectionUrl);
            string host = uri.Host.ToLower();
            
            if (host.Contains("dev.azure.com"))
            {
                // Format: https://dev.azure.com/{organization}/
                var segments = uri.Segments;
                if (segments.Length > 1)
                {
                    return segments[1].Trim('/').ToLower();
                }
            }
            else if (host.Contains("visualstudio.com"))
            {
                // Format: https://{organization}.visualstudio.com/
                return host.Split('.')[0].ToLower();
            }
            
            return "";
        }

        private void LogAuthenticationError(string url, string tokenInfo)
        {
            Uri uri = new Uri(url);
            string org = ExtractOrganization(url);
            
            Log.LogError("Authentication failed for URL: {Url}", url);
            Log.LogError("Organization detected: {Org}", org);
            Log.LogError("Token was {TokenStatus}", string.IsNullOrEmpty(tokenInfo) ? "NOT PROVIDED" : "PROVIDED");
            
            string sourceOrg = ExtractOrganization(_processor.Source.Options.Collection.ToString());
            string targetOrg = ExtractOrganization(_processor.Target.Options.Collection.ToString());
            
            Log.LogError("Source Organization: {SourceOrg}, Target Organization: {TargetOrg}", sourceOrg, targetOrg);
            
            if (org.Equals(sourceOrg, StringComparison.OrdinalIgnoreCase))
            {
                Log.LogError("This appears to be a SOURCE organization URL. Check your source PAT token.");
                Log.LogError("Source Auth Mode: {Mode}", _processor.Source.Options.Authentication.AuthenticationMode);
            }
            else if (org.Equals(targetOrg, StringComparison.OrdinalIgnoreCase))
            {
                Log.LogError("This appears to be a TARGET organization URL. Check your target PAT token.");
                Log.LogError("Target Auth Mode: {Mode}", _processor.Target.Options.Authentication.AuthenticationMode);
            }
            else
            {
                Log.LogError("Could not match organization to source or target. This might be a third-party org.");
            }
        }

        private Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.AttachmentReference UploadImageToTarget(WorkItem wi, string filePath)
        {
            var httpClient = ((TfsConnection)_processor.Target.InternalCollection).GetClient<WorkItemTrackingHttpClient>();

            // uploads and creates the image attachment
            Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.AttachmentReference link = null;
            using (FileStream uploadStream = File.Open(filePath, FileMode.Open, FileAccess.Read))
            {
                link = httpClient.CreateAttachmentAsync(uploadStream, fileName: Path.GetFileName(filePath)).ConfigureAwait(false).GetAwaiter().GetResult();
            }

            if (link == null)
            {
                throw new Exception($"Problem uploading image [{filePath}] for Work Item [{wi.Id}].");
            }

            // Attaches it with dummy work item and removes it just to be able to make the image visible to all the users.
            // VS402330: Unauthorized Read access to the attachment under the areas
            var payload = new Microsoft.VisualStudio.Services.WebApi.Patch.Json.JsonPatchDocument();
            payload.Add(new Microsoft.VisualStudio.Services.WebApi.Patch.Json.JsonPatchOperation()
            {
                Operation = Microsoft.VisualStudio.Services.WebApi.Patch.Operation.Add,
                Path = "/relations/-",
                Value = new
                {
                    rel = "AttachedFile",
                    url = link.Url
                }
            });

            var dummyWi = GetDummyWorkItem(wi.Type);
            var wii = httpClient.UpdateWorkItemAsync(payload, dummyWi.Id, bypassRules: true).GetAwaiter().GetResult();
            if (wii != null)
            {
                payload[0].Operation = Microsoft.VisualStudio.Services.WebApi.Patch.Operation.Remove;
                payload[0].Path = "/relations/" + (wii.Relations.Count - 1);
                payload[0].Value = null;
                wii = httpClient.UpdateWorkItemAsync(payload, dummyWi.Id, bypassRules: true).GetAwaiter().GetResult();
            }
            else
            {
                throw new Exception($"Problem attaching the uploaded image [{filePath}] with dummy workitem [{dummyWi.Id}] to be able to use for Work Item [{wi.Id}].");
            }

            return link;
        }


        private int _DummyWorkItemCount = 0;
        private TfsProcessor _processor;

        private WorkItem GetDummyWorkItem(WorkItemType type = null)
        {
            if (_DummyWorkItemCount > 900)
            {
                Log.LogDebug("EmbededImagesRepairEnricher: Dummy workitem {id} is neering capacity. Creating a new one!", _targetDummyWorkItem.Id);
                _targetDummyWorkItem.Close();
                _targetDummyWorkItem = null;
                _DummyWorkItemCount = 0;
            }
            if (_targetDummyWorkItem == null)
            {
                if (_targetProject.WorkItemTypes.Count == 0) return null;

                if (type == null)
                {
                    type = _targetProject.WorkItemTypes["Task"];
                }
                if (type == null)
                {
                    type = _targetProject.WorkItemTypes[0];
                }

                _targetDummyWorkItem = type.NewWorkItem();
                _targetDummyWorkItem.Title = TargetDummyWorkItemTitle;

                var fails = _targetDummyWorkItem.Validate();
                if (fails.Count > 0)
                {
                    Log.LogWarning("Dummy Work Item is not ready to save as it has some invalid fields. This may not result in an error. Enable LogLevel as 'Debug' in the config to see more.");
                    Log.LogDebug("--------------------------------------------------------------------------------------------------------------------");
                    Log.LogDebug("--------------------------------------------------------------------------------------------------------------------");
                    foreach (Field f in fails)
                    {
                        Log.LogDebug("Invalid Field Object:\r\n{Field}", f.ToJson());
                    }
                    Log.LogDebug("--------------------------------------------------------------------------------------------------------------------");
                    Log.LogDebug("--------------------------------------------------------------------------------------------------------------------");
                }
                Log.LogTrace("TfsEmbededImagesTool::GetDummyWorkItem::Save()");


                _targetDummyWorkItem.Save();

                if (_targetDummyWorkItem.Id == 0)
                {
                    throw new Exception("The Dummy work Item cant be created due to a save failure. This is likley due to required fields on the Task or First work items type.");
                }
                else
                {
                    Log.LogDebug("TfsEmbededImagesTool: Dummy workitem {id} created on the target collection.", _targetDummyWorkItem.Id);
                    _allDummyWorkItemIds.Add(_targetDummyWorkItem.Id);
                }
            }
            _DummyWorkItemCount++;
            return _targetDummyWorkItem;
        }
    }
}
