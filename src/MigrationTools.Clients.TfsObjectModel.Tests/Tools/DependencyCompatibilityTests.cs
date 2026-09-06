using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NCalc;

namespace MigrationTools.Tools.Tests
{
    [TestClass]
    public class DependencyCompatibilityTests
    {
        [TestMethod, TestCategory("L0")]
        public void PatchedExpressionEnginePreservesArithmeticAndParameters()
        {
            var expression = new Expression("[hours] * 60 + 5");
            expression.Parameters["hours"] = 2;
            Assert.AreEqual(125d, Convert.ToDouble(expression.Evaluate()));
            Assert.AreEqual(14d, Convert.ToDouble(new Expression("2 + 3 * 4").Evaluate()));
        }

        [TestMethod, TestCategory("L0"), Timeout(3000)]
        public void FactorialBoundIsEnforcedWithoutEvaluatingAnUnboundedInput()
        {
            // Even the old implementation finishes 171 iterations; do not use
            // an unbounded exploit as a regression fixture.
            Exception failure = null;
            try { new Expression("171!").Evaluate(); }
            catch (Exception ex) { failure = ex; }
            Assert.IsNotNull(failure, "An unsupported factorial must be rejected");
        }

        [TestMethod, TestCategory("L0")]
        public void PatchedJwtLibrariesRoundTripLocalTestData()
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.WriteToken(new JwtSecurityToken(claims: new[] { new Claim("fixture", "offline") }));
            Assert.IsTrue(handler.CanReadToken(token));
            Assert.AreEqual("offline", new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("fixture").Value);
        }
    }
}
