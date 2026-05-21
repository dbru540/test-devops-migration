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

    }
}
