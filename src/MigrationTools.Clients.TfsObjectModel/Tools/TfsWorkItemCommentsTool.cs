using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MigrationTools.Clients;
using MigrationTools.DataContracts;
using MigrationTools.Endpoints;
using MigrationTools.Services;
using MigrationTools.Tools.Infrastructure;
using Newtonsoft.Json.Linq;

namespace MigrationTools.Tools
{
    public class TfsWorkItemCommentsTool : Tool<TfsWorkItemCommentsToolOptions>
    {
        private const string SyncedCommentMarker = "<!-- SYNCED -->";
        private const string BackfilledCommentMarker = "<!-- BACKFILLED -->";

        private static readonly Regex LegacySyncedCommentPrefixRegex = new Regex(
            @"^\s*<b>\[Synced - Comment by .*?\]</b><br\/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BackfilledCommentPrefixRegex = new Regex(
            @"^\s*<b>\[BACKFILLED COMMENT - Source rev .*?\]</b><br\/?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AzureDevOpsWorkItemUrlRegex = new Regex(
            @"https://dev\.azure\.com/[^""'<>]+?/_workitems/edit/(?<id>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AzureDevOpsAttachmentUrlRegex = new Regex(
            @"https://dev\.azure\.com/(?<org>[^/\s]+)/(?<project>[^/\s]+)/_apis/wit/attachments/(?<id>[^?\s/]+)(?<query>\?[^\s<>""]+)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AzureDevOpsWorkItemWidgetRegex = new Regex(
            @"<!--MentionBegin-->.*?mention-widget-workitem.*?<!--MentionEnd-->",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex AzureDevOpsWorkItemWidgetAnchorRegex = new Regex(
            @"<a\b[^>]*class=""[^""]*mention-widget-workitem[^""]*""[^>]*>.*?</a>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public class CommentInfo
        {
            public int Id { get; set; }
            public string Author { get; set; }
            public DateTime CreatedDateUtc { get; set; }
            public string Format { get; set; }
            public string RawText { get; set; }
            public string CanonicalText { get; set; }
        }

        public TfsWorkItemCommentsTool(
            IOptions<TfsWorkItemCommentsToolOptions> options,
            IServiceProvider services,
            ILogger<TfsWorkItemCommentsTool> logger,
            ITelemetryLogger telemetry) : base(options, services, logger, telemetry)
        {
        }

        public async Task<IReadOnlyList<CommentInfo>> GetCommentsAsync(TfsTeamProjectEndpoint endpoint, int workItemId)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            var commentInfos = new List<CommentInfo>();
            var comments = await GetCommentsInternalAsync(endpoint, workItemId, excludeSyncAccountAuthoredComments: true).ConfigureAwait(false);
            commentInfos.AddRange(comments);

            return commentInfos;
        }

        public async Task<IReadOnlyList<CommentInfo>> GetMissingCommentsAsync(TfsTeamProjectEndpoint source, int sourceWorkItemId, TfsTeamProjectEndpoint target, int targetWorkItemId)
        {
            var sourceComments = await GetCommentsInternalAsync(source, sourceWorkItemId, excludeSyncAccountAuthoredComments: true).ConfigureAwait(false);
            var targetComments = await GetCommentsInternalAsync(target, targetWorkItemId, excludeSyncAccountAuthoredComments: false).ConfigureAwait(false);
            return GetMissingComments(sourceComments, targetComments);
        }

        internal static IReadOnlyList<CommentInfo> GetMissingComments(IEnumerable<CommentInfo> sourceComments, IEnumerable<CommentInfo> targetComments)
        {
            var targetCounts = targetComments
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.CanonicalText))
                .GroupBy(x => x.CanonicalText, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

            var missing = new List<CommentInfo>();
            foreach (var sourceComment in sourceComments.Where(x => x != null && !string.IsNullOrWhiteSpace(x.CanonicalText)).OrderBy(x => x.CreatedDateUtc))
            {
                if (!targetCounts.TryGetValue(sourceComment.CanonicalText, out int count) || count == 0)
                {
                    missing.Add(sourceComment);
                    continue;
                }

                targetCounts[sourceComment.CanonicalText] = count - 1;
            }

            return missing;
        }

        public async Task<int> CopyMissingCommentsAsync(TfsTeamProjectEndpoint source, int sourceWorkItemId, TfsTeamProjectEndpoint target, int targetWorkItemId)
        {
            var missingComments = await GetMissingCommentsAsync(source, sourceWorkItemId, target, targetWorkItemId).ConfigureAwait(false);
            if (missingComments.Count == 0)
            {
                return 0;
            }

            string project = Uri.EscapeDataString(target.Options.Project);
            string requestUri = $"{target.Options.Collection.AbsoluteUri.TrimEnd('/')}/{project}/_apis/wit/workItems/{targetWorkItemId}/comments?api-version=7.1-preview.4";

            using (var client = CreateHttpClient(target.Options))
            {
                foreach (var comment in missingComments)
                {
                    var payload = BuildCommentPayload(RewriteCommentForTarget(comment.RawText, source, target), comment.Format);

                    using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
                    using (var response = await client.PostAsync(requestUri, content).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                    }
                }
            }

            return missingComments.Count;
        }

        public async Task PostCommentAsync(TfsTeamProjectEndpoint target, int targetWorkItemId, string commentText)
        {
            string project = Uri.EscapeDataString(target.Options.Project);
            string requestUri = $"{target.Options.Collection.AbsoluteUri.TrimEnd('/')}/{project}/_apis/wit/workItems/{targetWorkItemId}/comments?api-version=7.1-preview.4";

            using (var client = CreateHttpClient(target.Options))
            {
                var payload = BuildCommentPayload(commentText, null);

                using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
                using (var response = await client.PostAsync(requestUri, content).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        internal static string NormalizeCommentForComparison(string commentText)
        {
            return NormalizeCommentForComparison(null, commentText);
        }

        internal static string NormalizeCommentForComparison(TfsTeamProjectEndpoint endpoint, string commentText)
        {
            if (string.IsNullOrWhiteSpace(commentText))
            {
                return string.Empty;
            }

            string normalized = commentText
                .Replace(SyncedCommentMarker, string.Empty)
                .Replace(BackfilledCommentMarker, string.Empty)
                .Trim();

            normalized = LegacySyncedCommentPrefixRegex.Replace(normalized, string.Empty).Trim();
            normalized = BackfilledCommentPrefixRegex.Replace(normalized, string.Empty).Trim();
            normalized = AzureDevOpsWorkItemWidgetRegex.Replace(normalized, " [ado-wi-link] ");
            normalized = AzureDevOpsWorkItemWidgetAnchorRegex.Replace(normalized, " [ado-wi-link] ");
            normalized = NormalizeSignificantHtmlElements(endpoint, normalized);
            normalized = NormalizePlainTextWorkItemUrls(endpoint, normalized);
            normalized = Regex.Replace(normalized, "<[^>]+>", " ");
            normalized = WebUtility.HtmlDecode(normalized);
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();

            return normalized;
        }

        private static string NormalizeSignificantHtmlElements(TfsTeamProjectEndpoint endpoint, string commentText)
        {
            if (string.IsNullOrWhiteSpace(commentText))
            {
                return string.Empty;
            }

            string normalized = Regex.Replace(commentText, "<img\\b[^>]*>", match =>
            {
                string tag = match.Value;
                string src = ExtractAttribute(tag, "src");
                string width = ExtractAttribute(tag, "width");
                string height = ExtractAttribute(tag, "height");
                string alt = ExtractAttribute(tag, "alt");
                return $" [img key={NormalizeAttachmentReference(src)} width={width} height={height} alt={alt}] ";
            }, RegexOptions.IgnoreCase);

            normalized = Regex.Replace(normalized, "<a\\b[^>]*href\\s*=\\s*[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", match =>
            {
                string href = match.Groups["href"].Value;
                string text = Regex.Replace(match.Groups["text"].Value, "<[^>]+>", " ");
                text = WebUtility.HtmlDecode(text);
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (text.StartsWith("@", StringComparison.Ordinal))
                {
                    return $" [mention {text}] ";
                }
                if (string.IsNullOrWhiteSpace(href) || href == "#")
                {
                    return $" {text} ";
                }
                string canonicalHref = NormalizeWorkItemReference(endpoint, href);
                if (!string.Equals(canonicalHref, href, StringComparison.Ordinal))
                {
                    return $" {canonicalHref} ";
                }
                canonicalHref = NormalizeAttachmentReference(href);
                return $" [link href={canonicalHref} text={text}] ";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            return normalized;
        }

        private static string NormalizePlainTextWorkItemUrls(TfsTeamProjectEndpoint endpoint, string commentText)
        {
            if (string.IsNullOrWhiteSpace(commentText))
            {
                return string.Empty;
            }

            return AzureDevOpsWorkItemUrlRegex.Replace(commentText, match => NormalizeWorkItemReference(endpoint, match.Value));
        }

        private static string NormalizeWorkItemReference(TfsTeamProjectEndpoint endpoint, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var match = AzureDevOpsWorkItemUrlRegex.Match(value);
            if (!match.Success)
            {
                return value;
            }

                return "[ado-wi-link]";
            }

        private static string NormalizeAttachmentReference(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var match = AzureDevOpsAttachmentUrlRegex.Match(value);
            if (!match.Success)
            {
                return value;
            }

            string query = match.Groups["query"].Value;
            string fileName = string.Empty;
            if (!string.IsNullOrWhiteSpace(query))
            {
                foreach (string part in query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int separatorIndex = part.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    string key = Uri.UnescapeDataString(part.Substring(0, separatorIndex));
                    if (!key.Equals("fileName", StringComparison.OrdinalIgnoreCase) &&
                        !key.Equals("filename", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    fileName = Uri.UnescapeDataString(part.Substring(separatorIndex + 1));
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = match.Groups["id"].Value;
            }

            return $"[ado-attachment:{fileName}]";
        }

        private static string ExtractAttribute(string htmlTag, string attributeName)
        {
            var match = Regex.Match(htmlTag, attributeName + "\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.IgnoreCase);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : string.Empty;
        }

        internal static bool IsMigrationGeneratedCommentContent(string commentText)
        {
            if (string.IsNullOrWhiteSpace(commentText))
            {
                return false;
            }

            return commentText.IndexOf(SyncedCommentMarker, StringComparison.Ordinal) >= 0
                || commentText.IndexOf(BackfilledCommentMarker, StringComparison.Ordinal) >= 0
                || LegacySyncedCommentPrefixRegex.IsMatch(commentText)
                || BackfilledCommentPrefixRegex.IsMatch(commentText);
        }

        private static DateTime ParseDate(string value)
        {
            if (DateTime.TryParse(value, out DateTime parsed))
            {
                return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            }

            return DateTime.MinValue;
        }

        private static HttpClient CreateHttpClient(TfsTeamProjectEndpointOptions options)
        {
            var client = new HttpClient();
            string token = options.Authentication?.AccessToken ?? string.Empty;
            string basic = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
            return client;
        }

        internal static bool IsSyncAccountAuthor(string author, string syncAccount)
        {
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(syncAccount))
            {
                return false;
            }

            return NormalizeIdentity(author) == NormalizeIdentity(syncAccount);
        }

        private static string GetEndpointSyncAccountName(TfsTeamProjectEndpoint endpoint)
        {
            var collection = endpoint.InternalCollection as TfsTeamProjectCollection;
            return collection?.AuthorizedIdentity?.DisplayName?.Trim() ?? string.Empty;
        }

        private static string NormalizeIdentity(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            int atIndex = normalized.IndexOf('@');
            if (atIndex > 0)
            {
                normalized = normalized.Substring(0, atIndex);
            }

            normalized = Regex.Replace(normalized, @"[^a-zA-Z0-9]+", string.Empty);
            return normalized.ToLowerInvariant();
        }

        private static string RewriteCommentForTarget(string rawText, TfsTeamProjectEndpoint source, TfsTeamProjectEndpoint target)
        {
            // Preserve the original comment body for writes. URL/image translation will be
            // implemented here explicitly once the target-side rewrite rules are finalized.
            return rawText ?? string.Empty;
        }

        private async Task<IReadOnlyList<CommentInfo>> GetCommentsInternalAsync(TfsTeamProjectEndpoint endpoint, int workItemId, bool excludeSyncAccountAuthoredComments)
        {
            var commentInfos = new List<CommentInfo>();
            string syncAccount = GetEndpointSyncAccountName(endpoint);
            string project = Uri.EscapeDataString(endpoint.Options.Project);

            using (var client = CreateHttpClient(endpoint.Options))
            {
                string continuationToken = null;
                do
                {
                    string requestUri = $"{endpoint.Options.Collection.AbsoluteUri.TrimEnd('/')}/{project}/_apis/wit/workItems/{workItemId}/comments?$top=200&includeDeleted=false&api-version=7.1-preview.4";
                    if (!string.IsNullOrWhiteSpace(continuationToken))
                    {
                        requestUri += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
                    }

                    using (var response = await client.GetAsync(requestUri).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        string payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        JArray comments = (JArray)JObject.Parse(payload)["comments"];
                        if (comments != null)
                        {
                            foreach (JObject comment in comments.OfType<JObject>())
                            {
                                string rawText = comment.Value<string>("text") ?? string.Empty;
                                string author = comment["createdBy"]?["displayName"]?.Value<string>()?.Trim() ?? string.Empty;
                                if (Options.IgnoreMigrationGeneratedComments && IsMigrationGeneratedCommentContent(rawText))
                                {
                                    continue;
                                }
                                if (excludeSyncAccountAuthoredComments && Options.IgnoreMigrationGeneratedComments && IsSyncAccountAuthor(author, syncAccount))
                                {
                                    continue;
                                }

                                commentInfos.Add(new CommentInfo
                                {
                                    Id = comment.Value<int?>("id") ?? comment.Value<int?>("commentId") ?? 0,
                                    Author = author,
                                    CreatedDateUtc = ParseDate(comment.Value<string>("createdDate")),
                                    Format = comment.Value<string>("format") ?? string.Empty,
                                    RawText = rawText,
                                    CanonicalText = NormalizeCommentForComparison(endpoint, rawText)
                                });
                            }
                        }

                        continuationToken = null;
                        if (response.Headers.TryGetValues("x-ms-continuationtoken", out IEnumerable<string> continuationValues))
                        {
                            continuationToken = continuationValues.FirstOrDefault();
                        }
                    }
                } while (!string.IsNullOrWhiteSpace(continuationToken));
            }

            return commentInfos;
        }

        private static JObject BuildCommentPayload(string commentText, string format)
        {
            var payload = new JObject
            {
                ["text"] = commentText ?? string.Empty
            };

            if (!string.IsNullOrWhiteSpace(format))
            {
                payload["format"] = format;
            }

            return payload;
        }
    }
}
