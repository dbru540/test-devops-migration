using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MigrationTools.Tools.Tests
{
    [TestClass]
    public class TfsEmbededImagesToolTests
    {
        [TestMethod("TfsEmbededImagesToolTests_WorkItemLinksAreNotEmbeddedImageChanges"), TestCategory("L0")]
        public void WorkItemLinksAreNotEmbeddedImageChanges()
        {
            MethodInfo method = typeof(TfsEmbededImagesTool).GetMethod("IsWrongOrganizationAttachmentUrl", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsFalse((bool)method.Invoke(null, new object[]
            {
                "https://dev.azure.com/fiveforty/Cityz%20Media%20-%20Projet%20Facturation/_workitems/edit/185123",
                "solution-d365"
            }));
        }

        [TestMethod("TfsEmbededImagesToolTests_WrongOrganizationAttachmentsAreEmbeddedImageChanges"), TestCategory("L0")]
        public void WrongOrganizationAttachmentsAreEmbeddedImageChanges()
        {
            MethodInfo method = typeof(TfsEmbededImagesTool).GetMethod("IsWrongOrganizationAttachmentUrl", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsTrue((bool)method.Invoke(null, new object[]
            {
                "https://dev.azure.com/fiveforty/_apis/wit/attachments/0f5f2e6c-8a9f-4d25-8c0f-123456789abc?fileName=image.png",
                "solution-d365"
            }));
        }

        [TestMethod("TfsEmbededImagesToolTests_TargetOrganizationAttachmentsAreNotEmbeddedImageChanges"), TestCategory("L0")]
        public void TargetOrganizationAttachmentsAreNotEmbeddedImageChanges()
        {
            MethodInfo method = typeof(TfsEmbededImagesTool).GetMethod("IsWrongOrganizationAttachmentUrl", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsFalse((bool)method.Invoke(null, new object[]
            {
                "https://dev.azure.com/solution-d365/_apis/wit/attachments/0f5f2e6c-8a9f-4d25-8c0f-123456789abc?fileName=image.png",
                "solution-d365"
            }));
        }
    }
}
