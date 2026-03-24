using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MigrationTools.Tools;

namespace MigrationTools.Processors.Tests
{
    [TestClass]
    public class TfsCommentSyncServiceTests
    {
        [TestMethod, TestCategory("L0")]
        public void TfsWorkItemCommentsTool_NormalizeCommentForComparison_NormalizesMentionMarkup()
        {
            var method = typeof(TfsWorkItemCommentsTool).GetMethod("NormalizeCommentForComparison", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(method);

            const string source = "<div><a href=\"#\" data-vss-mention=\"version:2.0,e652503b-43d7-632a-b679-3116a3a7d576\">@Joseph Dumoulin</a>&nbsp;C’est bon pour moi : les tests de non-régression sont finalisés. Le ticket peut être livré en AUT. </div><div>Merci </div>";
            const string target = "[mention @Joseph Dumoulin] C’est bon pour moi : les tests de non-régression sont finalisés. Le ticket peut être livré en AUT. Merci";

            var normalizedSource = (string)method.Invoke(null, new object[] { source });
            var normalizedTarget = (string)method.Invoke(null, new object[] { target });

            Assert.AreEqual(target, normalizedSource);
            Assert.AreEqual(normalizedSource, normalizedTarget);
        }

        [TestMethod, TestCategory("L0")]
        public void TfsWorkItemCommentsTool_IsMigrationGeneratedCommentContent_RecognizesBackfilledAndSyncedMarkers()
        {
            var method = typeof(TfsWorkItemCommentsTool).GetMethod("IsMigrationGeneratedCommentContent", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            Assert.IsTrue((bool)method.Invoke(null, new object[] { "<!-- BACKFILLED --><b>[BACKFILLED COMMENT - Source rev 10 - Original date 2026-03-23 21:35:03 UTC - Author Test]</b><br/>hello" }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "<!-- SYNCED --><div>hello</div>" }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "@Joseph Dumoulin C’est bon pour moi" }));
        }

        [TestMethod, TestCategory("L0")]
        public void TfsWorkItemCommentsTool_IsSyncAccountAuthor_MatchesAliasAndEmailVariant()
        {
            var method = typeof(TfsWorkItemCommentsTool).GetMethod("IsSyncAccountAuthor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            Assert.IsTrue((bool)method.Invoke(null, new object[] { "svc- msflow", "svc-msflow@fiveforty.fr" }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "svc-msflow@fiveforty.fr", "svc- msflow" }));
        }

        [TestMethod, TestCategory("L0")]
        public void TfsWorkItemCommentsTool_GetMissingComments_UsesOneToOneCountMatching()
        {
            var method = typeof(TfsWorkItemCommentsTool).GetMethod("GetMissingComments", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var sourceComments = new[]
            {
                new TfsWorkItemCommentsTool.CommentInfo
                {
                    Id = 1,
                    Author = "Joseph Dumoulin",
                    CreatedDateUtc = new DateTime(2026, 3, 23, 14, 58, 56, DateTimeKind.Utc),
                    CanonicalText = "@Nermine Ayadi pour le suivi",
                    RawText = "<div>@Nermine Ayadi pour le suivi</div>"
                }
            };

            var targetComments = new[]
            {
                new TfsWorkItemCommentsTool.CommentInfo
                {
                    Id = 99,
                    Author = "Joseph Dumoulin",
                    CreatedDateUtc = new DateTime(2026, 3, 23, 14, 59, 30, DateTimeKind.Utc),
                    CanonicalText = "@Nermine Ayadi pour le suivi",
                    RawText = "@Nermine Ayadi pour le suivi"
                }
            };

            var missing = (System.Collections.Generic.IReadOnlyList<TfsWorkItemCommentsTool.CommentInfo>)method.Invoke(null, new object[] { sourceComments, targetComments });

            Assert.AreEqual(0, missing.Count);
        }

        [TestMethod, TestCategory("L0")]
        public void TfsWorkItemCommentsTool_GetMissingComments_ReturnsDeficitOnly()
        {
            var method = typeof(TfsWorkItemCommentsTool).GetMethod("GetMissingComments", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var sourceComments = new[]
            {
                new TfsWorkItemCommentsTool.CommentInfo
                {
                    Id = 1,
                    Author = "Nicolas Condomines",
                    CreatedDateUtc = new DateTime(2026, 3, 23, 14, 30, 30, DateTimeKind.Utc),
                    CanonicalText = "Associated with changeset 2422: 185193",
                    RawText = "Associated with changeset 2422: 185193"
                },
                new TfsWorkItemCommentsTool.CommentInfo
                {
                    Id = 2,
                    Author = "Nicolas Condomines",
                    CreatedDateUtc = new DateTime(2026, 3, 23, 16, 03, 59, DateTimeKind.Utc),
                    CanonicalText = "Associated with changeset 2423: release 1046.3.0",
                    RawText = "Associated with changeset 2423: release 1046.3.0"
                }
            };

            var targetComments = new[]
            {
                new TfsWorkItemCommentsTool.CommentInfo
                {
                    Id = 99,
                    Author = "Nicolas Condomines",
                    CreatedDateUtc = new DateTime(2026, 3, 23, 14, 31, 00, DateTimeKind.Utc),
                    CanonicalText = "Associated with changeset 2422: 185193",
                    RawText = "Associated with changeset 2422: 185193"
                }
            };

            var missing = (System.Collections.Generic.IReadOnlyList<TfsWorkItemCommentsTool.CommentInfo>)method.Invoke(null, new object[] { sourceComments, targetComments });

            Assert.AreEqual(1, missing.Count);
            Assert.AreEqual(2, missing[0].Id);
        }

        [TestMethod, TestCategory("L0")]
        public void TfsWorkItemCommentsTool_NormalizeCommentForComparison_PreservesImageIdentity()
        {
            var method = typeof(TfsWorkItemCommentsTool).GetMethod("NormalizeCommentForComparison", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(method);

            const string withImageA = "<div>See screenshot</div><img src=\"https://dev.azure.com/fiveforty/_apis/wit/attachments/a1?fileName=x.png\" width=\"120\" height=\"80\" alt=\"x\" />";
            const string withImageB = "<p>See screenshot</p><img alt=\"x\" height=\"80\" width=\"120\" src=\"https://dev.azure.com/fiveforty/_apis/wit/attachments/a1?fileName=x.png\">";
            const string withDifferentImage = "<div>See screenshot</div><img src=\"https://dev.azure.com/fiveforty/_apis/wit/attachments/b2?fileName=y.png\" width=\"120\" height=\"80\" alt=\"x\" />";

            var normalizedA = (string)method.Invoke(null, new object[] { withImageA });
            var normalizedB = (string)method.Invoke(null, new object[] { withImageB });
            var normalizedDifferent = (string)method.Invoke(null, new object[] { withDifferentImage });

            Assert.AreEqual(normalizedA, normalizedB);
            Assert.AreNotEqual(normalizedA, normalizedDifferent);
        }

        [TestMethod, TestCategory("L0")]
        public void TfsWorkItemCommentsTool_NormalizeCommentForComparison_NormalizesAzureDevOpsWorkItemUrls()
        {
            var method = typeof(TfsWorkItemCommentsTool).GetMethod("NormalizeCommentForComparison", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(method);

            const string source = "@David Bru TEST https://dev.azure.com/fiveforty/Christofle/_workitems/edit/185193";
            const string target = "@David Bru TEST https://dev.azure.com/CHRISTOFLE/ERP%20program/_workitems/edit/91168";

            var normalizedSource = (string)method.Invoke(null, new object[] { source });
            var normalizedTarget = (string)method.Invoke(null, new object[] { target });

            Assert.AreEqual(normalizedSource, normalizedTarget);
            StringAssert.Contains(normalizedSource, "[ado-wi-link]");
        }
    }
}
