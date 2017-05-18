#region LICENSE

/*
    
    Copyright(c) Voat, Inc.

    This file is part of Voat.

    This source file is subject to version 3 of the GPL license,
    that is bundled with this package in the file LICENSE, and is
    available online at http://www.gnu.org/licenses/gpl-3.0.txt;
    you may not use this file except in compliance with the License.

    Software distributed under the License is distributed on an
    "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, either express
    or implied. See the License for the specific language governing
    rights and limitations under the License.

    All Rights Reserved.

*/

#endregion LICENSE

using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.EntityFramework;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Voat.Data.Models;
using Voat.Utilities;

namespace Voat.Tests.Utils
{
    [TestClass]
    public class AccountTests : BaseUnitTest
    {

        [TestMethod]
        [TestCategory("Utility"), TestCategory("Account")]
        public void TestIsPasswordComplex()
        {
            const string testPassword1 = "donthackmeplox";
            const string testPassword2 = "tHisIs4P4ssw0rd!";

            const string testUsername1 = "donthackmeplox";
            const string testUsername2 = "TheSpoon";

            bool result1 = AccountSecurity.IsPasswordComplex(testPassword1, testUsername1);
            Assert.AreEqual(false, result1, "Unable to check password complexity for insecure password.");

            bool result2 = AccountSecurity.IsPasswordComplex(testPassword2, testUsername2);
            Assert.AreEqual(true, result2, "Unable to check password complexity for secure password.");

            bool result3 = AccountSecurity.IsPasswordComplex(testPassword1, testUsername2);
            Assert.AreEqual(false, result3, "Unable to check password complexity for insecure password.");
        }

        [TestMethod]
        [TestCategory("Utility"), TestCategory("Account")]
        public void TestUserNameAvailability()
        {
            var originalUserName = "IHeartFuzzyLOL";

            using (var userManager = new UserManager<VoatUser>(new UserStore<VoatUser>(new ApplicationDbContext())))
            {
                var user = new VoatUser
                {
                    UserName = originalUserName,
                    RegistrationDateTime = DateTime.UtcNow,
                    LastLoginFromIp = "127.0.0.1",
                    LastLoginDateTime = DateTime.UtcNow
                };

                // try to create new user account
                var createResult = userManager.Create(user, "topsecretpasswordgoeshere");

                Assert.AreEqual(true, createResult.Succeeded);


                var response = UserHelper.CanUserNameBeRegistered(userManager, originalUserName, null);
                Assert.AreEqual(false, response);

                response = UserHelper.CanUserNameBeRegistered(userManager, "iheartfuzzylol", null); //test casing
                Assert.AreEqual(false, response);

                response = UserHelper.CanUserNameBeRegistered(userManager, "lheartfuzzylol", null); //test default reg
                Assert.AreEqual(true, response);


                Dictionary<string, string> charSwaps = new Dictionary<string, string>();
                charSwaps.Add("i", "l");
                charSwaps.Add("o", "0");
                charSwaps.Add("h", "hahaha"); //just to make sure offset swapping does not break
                charSwaps.Add("heart", "like"); //just to make sure offset swapping does not break

                var spoofs = UserHelper.DefaultSpoofList(charSwaps);
                

                response = UserHelper.CanUserNameBeRegistered(userManager, originalUserName, spoofs); 
                Assert.AreEqual(false, response);
                                                                            
                response = UserHelper.CanUserNameBeRegistered(userManager, "iheartfuzzyIoI", spoofs); 
                Assert.AreEqual(false, response);

                response = UserHelper.CanUserNameBeRegistered(userManager, "lheartfuzzyLOL", spoofs); 
                Assert.AreEqual(false, response);

                response = UserHelper.CanUserNameBeRegistered(userManager, "lheartFuzzyIOi", spoofs);
                Assert.AreEqual(false, response);

                response = UserHelper.CanUserNameBeRegistered(userManager, "lheartFuzzyl0i", spoofs);
                Assert.AreEqual(false, response);

            }
        }
    }
}
