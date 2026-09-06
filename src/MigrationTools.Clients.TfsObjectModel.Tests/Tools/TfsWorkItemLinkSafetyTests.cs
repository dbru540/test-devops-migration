using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MigrationTools.Tools.Tests
{
    [TestClass]
    public class TfsWorkItemLinkSafetyTests
    {
        private static void CheckParent(bool preserve, int current, int requested)
        {
            MethodInfo method = typeof(TfsWorkItemLinkTool).GetMethod(
                "EnsureParentReplacementAllowed", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            method.Invoke(null, new object[] { preserve, current, requested });
        }

        [TestMethod, TestCategory("L0")]
        public void IncrementalLinkCatchupDoesNotReplaceAnotherParent()
        {
            TargetInvocationException error = Assert.ThrowsException<TargetInvocationException>(
                () => CheckParent(true, 127, 130));
            Assert.IsInstanceOfType(error.InnerException, typeof(InvalidOperationException));
        }

        [TestMethod, TestCategory("L0")]
        public void ExistingSameParentIsIdempotent()
        {
            CheckParent(true, 127, 127);
        }

        [TestMethod, TestCategory("L0")]
        public void BulkMigrationRetainsItsExplicitReplacementPolicy()
        {
            CheckParent(false, 127, 130);
        }
    }
}
