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

    }
}
