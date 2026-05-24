using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MigrationTools.DataContracts;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MigrationTools.Processors.Tests
{
    [TestClass()]
    public class TfsWorkItemMigrationProcessorTests
    {
        [TestInitialize]
        public void ConfigureTestEnvironment()
        {
            Environment.SetEnvironmentVariable("COMMENT_SYNC_CUTOFF", "2026-01-01T00:00:00Z");
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_OptionsValidator_Empty"), TestCategory("L0")]
        public void OptionsValidator_Empty()
        {
            var validator = new TfsWorkItemMigrationProcessorOptionsValidator();
            var x = new TfsWorkItemMigrationProcessorOptions();
            Assert.IsTrue(validator.Validate(null, x).Failed);
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_OptionsValidator_Valid"), TestCategory("L0")]
        public void OptionsValidator_Valid()
        {
            var validator = new TfsWorkItemMigrationProcessorOptionsValidator();
            var x = new TfsWorkItemMigrationProcessorOptions();
            x.WIQLQuery = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @TeamProject";
            x.SourceName = "source";
            x.TargetName = "target";
            ValidateOptionsResult result = validator.Validate(null, x);
            Assert.IsTrue(result.Succeeded);
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_Antiloop_Uses_AuthorizedAs"), TestCategory("L0")]
        public void Antiloop_Uses_AuthorizedAs()
        {
            Environment.SetEnvironmentVariable("ANTILOOP_ACCOUNTS", "svc-msflow@fiveforty.fr,svc-d365-devops@cityzmedia.fr,Migration");
            Environment.SetEnvironmentVariable("COMMENT_SYNC_CUTOFF", "2026-01-01T00:00:00Z");
            RevisionItem revision = new RevisionItem
            {
                Number = 33,
                Fields = new Dictionary<string, FieldItem>
                {
                    ["System.ChangedBy"] = new FieldItem { Value = "Nicolas Condomines <ncondomines@fiveforty.fr>" },
                    ["System.AuthorizedAs"] = new FieldItem { Value = "Service Account D365 Azure DevOps <svc-d365-devops@cityzmedia.fr>" },
                    ["System.History"] = new FieldItem { Value = "" },
                },
            };

            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("IsSyncGeneratedRevision", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsTrue((bool)method.Invoke(null, new object[] { revision }));
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_ObjectModel_Does_Not_Replay_PostCutoff_History"), TestCategory("L0")]
        public void ObjectModel_Does_Not_Replay_PostCutoff_History()
        {
            RevisionItem revision = new RevisionItem
            {
                Number = 38,
                ChangedDate = new DateTime(2026, 05, 21, 18, 48, 53, DateTimeKind.Utc),
                Fields = new Dictionary<string, FieldItem>
                {
                    ["System.History"] = new FieldItem { Value = "<div>test </div>" },
                },
            };

            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("ShouldApplyHistoryViaObjectModel", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsFalse((bool)method.Invoke(null, new object[] { revision, new DateTime(2026, 01, 01, 0, 0, 0, DateTimeKind.Utc) }));
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_ObjectModel_Field_Classifier_Excludes_Comment_Metadata"), TestCategory("L0")]
        public void ObjectModel_Field_Classifier_Excludes_Comment_Metadata()
        {
            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("IsObjectModelReplayField", BindingFlags.NonPublic | BindingFlags.Static);
            var ignoredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.CommentCount",
                "System.AuthorizedDate",
            };

            Assert.IsNotNull(method);
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "System.History", ignoredFields }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "System.ChangedBy", ignoredFields }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "System.ChangedDate", ignoredFields }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "System.CommentCount", ignoredFields }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "Microsoft.VSTS.Scheduling.RemainingWork", ignoredFields }));
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_ObjectModel_Replays_PostCutoff_Business_Fields_Without_History"), TestCategory("L0")]
        public void ObjectModel_Replays_PostCutoff_Business_Fields_Without_History()
        {
            RevisionItem revision = new RevisionItem
            {
                Number = 36,
                ChangedDate = new DateTime(2026, 05, 21, 18, 48, 53, DateTimeKind.Utc),
                Fields = new Dictionary<string, FieldItem>
                {
                    ["System.History"] = new FieldItem { Value = "<div>commentaire avec Task 185123</div>" },
                    ["System.CommentCount"] = new FieldItem { Value = 15 },
                    ["System.ChangedBy"] = new FieldItem { Value = "Nicolas Condomines <ncondomines@fiveforty.fr>" },
                    ["System.ChangedDate"] = new FieldItem { Value = "2026-05-21T14:25:17.673Z" },
                    ["Microsoft.VSTS.Scheduling.RemainingWork"] = new FieldItem { Value = 40 },
                    ["Microsoft.VSTS.Scheduling.OriginalEstimate"] = new FieldItem { Value = 40 },
                },
            };
            var ignoredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.CommentCount",
            };

            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("RevisionHasObjectModelReplayChanges", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsTrue((bool)method.Invoke(null, new object[] { revision, ignoredFields }));
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_ObjectModel_Skips_PostCutoff_Comment_Only_Revision"), TestCategory("L0")]
        public void ObjectModel_Skips_PostCutoff_Comment_Only_Revision()
        {
            RevisionItem revision = new RevisionItem
            {
                Number = 38,
                ChangedDate = new DateTime(2026, 05, 21, 18, 48, 53, DateTimeKind.Utc),
                Fields = new Dictionary<string, FieldItem>
                {
                    ["System.History"] = new FieldItem { Value = "<div>test </div>" },
                    ["System.CommentCount"] = new FieldItem { Value = 16 },
                    ["System.ChangedBy"] = new FieldItem { Value = "BRU, DAVID" },
                    ["System.ChangedDate"] = new FieldItem { Value = "2026-05-21T18:48:53.853Z" },
                    ["System.AuthorizedDate"] = new FieldItem { Value = "2026-05-21T18:48:53.913Z" },
                    ["System.Watermark"] = new FieldItem { Value = 1518281 },
                },
            };
            var ignoredFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System.CommentCount",
                "System.AuthorizedDate",
                "System.Watermark",
            };

            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("RevisionHasObjectModelReplayChanges", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsFalse((bool)method.Invoke(null, new object[] { revision, ignoredFields }));
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_EventDelta_Parses_Changed_Fields_JSON"), TestCategory("L0")]
        public void EventDelta_Parses_Changed_Fields_JSON()
        {
            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("GetEventDeltaFieldNames", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            var fields = (ICollection<string>)method.Invoke(null, new object[]
            {
                "{\"System.Title\":{\"oldValue\":\"A\",\"newValue\":\"B\"},\"System.History\":{\"newValue\":\"comment\"},\"Microsoft.VSTS.Common.Priority\":{\"oldValue\":2,\"newValue\":4}}"
            });

            CollectionAssert.AreEquivalent(
                new[] { "System.Title", "Microsoft.VSTS.Common.Priority" },
                fields.ToArray());
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_EventDelta_Decodes_Changed_Fields_JSON_Base64"), TestCategory("L0")]
        public void EventDelta_Decodes_Changed_Fields_JSON_Base64()
        {
            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("DecodeEventDeltaChangedFieldsJson", BindingFlags.NonPublic | BindingFlags.Static);
            string json = "{\"System.Title\":{\"oldValue\":\"Dossier d'affaires\",\"newValue\":\"Dossier \\\"final\\\"\"}}";
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            Assert.IsNotNull(method);
            string decoded = (string)method.Invoke(null, new object[] { encoded, "" });

            Assert.AreEqual(json, decoded);
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_EventDelta_Detects_Field_Conflict"), TestCategory("L0")]
        public void EventDelta_Detects_Field_Conflict()
        {
            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("IsEventDeltaConflict", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "2", "2", "4" }));
            Assert.IsFalse((bool)method.Invoke(null, new object[] { "2", "4", "4" }));
            Assert.IsTrue((bool)method.Invoke(null, new object[] { "2", "1", "4" }));
        }

        [TestMethod("TfsWorkItemMigrationProcessorTests_EventDelta_Builds_Conflict_Comment"), TestCategory("L0")]
        public void EventDelta_Builds_Conflict_Comment()
        {
            MethodInfo method = typeof(TfsWorkItemMigrationProcessor).GetMethod("BuildEventDeltaConflictComment", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            string comment = (string)method.Invoke(null, new object[]
            {
                "Microsoft.VSTS.Common.Priority",
                "2",
                "1",
                "4",
                "src",
                12
            });

            StringAssert.Contains(comment, "Sync conflict resolved");
            StringAssert.Contains(comment, "Microsoft.VSTS.Common.Priority");
            StringAssert.Contains(comment, "Expected previous value: 2");
            StringAssert.Contains(comment, "Conflicting target value: 1");
            StringAssert.Contains(comment, "Applied latest value: 4");
            StringAssert.Contains(comment, "latest event wins");
        }

    }
}
