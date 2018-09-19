﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using System.Web.Security;
using TwoFactorWeb.Models;
using System.Text;
using TwoFactor;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace TwoFactorWeb.Controllers
{
    public class AccountController : AsyncController
    {

        //
        // GET: /Account/LogOn

        public ActionResult LogOn()
        {
            return View();
        }

        //
        // POST: /Account/LogOn

        private void DoLogOn(LogOnModel model, string returnUrl)
        {
            try
            {
                if (ModelState.IsValid)
                {
                    if (Membership.ValidateUser(model.UserName, model.Password))
                    {
                        var profile = TwoFactorProfile.GetByUserName(model.UserName);

                        if (profile != null && !string.IsNullOrEmpty(profile.TwoFactorSecret))
                        {
                            // Prevent the user from attempting to brute force the two factor secret.
                            // Without this, an attacker, if they know your password already, could try to brute
                            // force the two factor code. They only need to try 1,000,000 distinct codes in 3 minutes.
                            // This throttles them down to a managable level.
                            if (profile.LastLoginAttemptUtc.HasValue && profile.LastLoginAttemptUtc > DateTime.UtcNow - TimeSpan.FromSeconds(1))
                            {
                                System.Threading.Thread.Sleep(5000);
                            }

                            profile.LastLoginAttemptUtc = DateTime.UtcNow;

                            if (TimeBasedOneTimePassword.IsValid(profile.TwoFactorSecret, model.TwoFactorCode))
                            {
                                if (Url.IsLocalUrl(returnUrl) && returnUrl.Length > 1 && returnUrl.StartsWith("/")
                                    && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\"))
                                {
                                    AsyncManager.Parameters["returnUrl"] = returnUrl;
                                }
                                else
                                {
                                    AsyncManager.Parameters["action"] = "Index";
                                    AsyncManager.Parameters["controller"] = "Home";
                                }
                            }
                            else
                            {
                                ModelState.AddModelError("", "The two factor code is incorrect.");
                            }
                        }
                        else
                        {
                            ModelState.AddModelError("", "The two factor code is incorrect.");
                        }
                    }
                    else
                    {
                        ModelState.AddModelError("", "The user name or password provided is incorrect.");
                    }
                }

                AsyncManager.Parameters["model"] = model;
            }
            finally
            {
                AsyncManager.OutstandingOperations.Decrement();
            }
        }

        [HttpPost]
        public void LogOnAsync(LogOnModel model, string returnUrl)
        {
            AsyncManager.OutstandingOperations.Increment();
            AsyncManager.Parameters["task"] = Task.Factory.StartNew(() => { DoLogOn(model, returnUrl); });
        }

        public ActionResult LogOnCompleted(Task task, string returnUrl, string action, string controller, LogOnModel model)
        {
            try
            {
                task.Wait();
            }
            catch (AggregateException ex)
            {
                Exception baseException = ex.GetBaseException();

                if (baseException is OneTimePasswordException)
                {
                    model = new LogOnModel();
                    ModelState.AddModelError("", "This two factor code has already been used. Please wait for the next code to be generated and try again.");
                }
                else
                {
                    throw;
                }
            }

            if (returnUrl != null)
            {
                FormsAuthentication.SetAuthCookie(model.UserName, model.RememberMe);
                return Redirect(returnUrl);
            }
            else if (action != null && controller != null)
            {
                FormsAuthentication.SetAuthCookie(model.UserName, model.RememberMe);
                return RedirectToAction(action, controller);
            }
            else
            {
                return View(model);
            }
        }

        //
        // GET: /Account/LogOff

        public ActionResult LogOff()
        {
            FormsAuthentication.SignOut();

            return RedirectToAction("Index", "Home");
        }

        //
        // GET: /Account/Register

        public ActionResult Register()
        {
            return View();
        }

        //
        // POST: /Account/Register

        [HttpPost]
        public ActionResult Register(RegisterModel model)
        {
            if (ModelState.IsValid)
            {
                // Attempt to register the user
                MembershipCreateStatus createStatus;
                var user = Membership.CreateUser(model.UserName, model.Password, model.Email, null, null, true, null, out createStatus);

                if (createStatus == MembershipCreateStatus.Success)
                {
                    FormsAuthentication.SetAuthCookie(model.UserName, false /* createPersistentCookie */);

                    return RedirectToAction("ShowTwoFactorSecret", "Account");
                }
                else
                {
                    ModelState.AddModelError("", ErrorCodeToString(createStatus));
                }
            }

            // If we got this far, something failed, redisplay form
            return View(model);
        }

        [Authorize]
        public ActionResult ShowTwoFactorSecret()
        {
            string secret = TwoFactorProfile.CurrentUser.TwoFactorSecret;

            if (string.IsNullOrEmpty(secret))
            {
                byte[] buffer = new byte[9];

                using (RandomNumberGenerator rng = RNGCryptoServiceProvider.Create())
                {
                    rng.GetBytes(buffer);
                }

                // Generates a 10 character string of A-Z, a-z, 0-9
                // Don't need to worry about any = padding from the
                // Base64 encoding, since our input buffer is divisible by 3
                TwoFactorProfile.CurrentUser.TwoFactorSecret = Convert.ToBase64String(buffer).Substring(0, 10).Replace('/', '0').Replace('+', '1');

                secret = TwoFactorProfile.CurrentUser.TwoFactorSecret;
            }

            var enc = new Base32Encoder().Encode(Encoding.ASCII.GetBytes(secret));

            return View(new TwoFactorSecret { EncodedSecret = enc });
        }

        //
        // GET: /Account/ChangePassword

        [Authorize]
        public ActionResult ChangePassword()
        {
            return View();
        }

        //
        // POST: /Account/ChangePassword

        [Authorize]
        [HttpPost]
        public ActionResult ChangePassword(ChangePasswordModel model)
        {
            if (ModelState.IsValid)
            {

                // ChangePassword will throw an exception rather
                // than return false in certain failure scenarios.
                bool changePasswordSucceeded;
                try
                {
                    MembershipUser currentUser = Membership.GetUser(User.Identity.Name, true /* userIsOnline */);
                    changePasswordSucceeded = currentUser.ChangePassword(model.OldPassword, model.NewPassword);
                }
                catch (Exception)
                {
                    changePasswordSucceeded = false;
                }

                if (changePasswordSucceeded)
                {
                    return RedirectToAction("ChangePasswordSuccess");
                }
                else
                {
                    ModelState.AddModelError("", "The current password is incorrect or the new password is invalid.");
                }
            }

            // If we got this far, something failed, redisplay form
            return View(model);
        }

        //
        // GET: /Account/ChangePasswordSuccess

        public ActionResult ChangePasswordSuccess()
        {
            return View();
        }

        #region Status Codes
        private static string ErrorCodeToString(MembershipCreateStatus createStatus)
        {
            // See http://go.microsoft.com/fwlink/?LinkID=177550 for
            // a full list of status codes.
            switch (createStatus)
            {
                case MembershipCreateStatus.DuplicateUserName:
                    return "User name already exists. Please enter a different user name.";

                case MembershipCreateStatus.DuplicateEmail:
                    return "A user name for that e-mail address already exists. Please enter a different e-mail address.";

                case MembershipCreateStatus.InvalidPassword:
                    return "The password provided is invalid. Please enter a valid password value.";

                case MembershipCreateStatus.InvalidEmail:
                    return "The e-mail address provided is invalid. Please check the value and try again.";

                case MembershipCreateStatus.InvalidAnswer:
                    return "The password retrieval answer provided is invalid. Please check the value and try again.";

                case MembershipCreateStatus.InvalidQuestion:
                    return "The password retrieval question provided is invalid. Please check the value and try again.";

                case MembershipCreateStatus.InvalidUserName:
                    return "The user name provided is invalid. Please check the value and try again.";

                case MembershipCreateStatus.ProviderError:
                    return "The authentication provider returned an error. Please verify your entry and try again. If the problem persists, please contact your system administrator.";

                case MembershipCreateStatus.UserRejected:
                    return "The user creation request has been canceled. Please verify your entry and try again. If the problem persists, please contact your system administrator.";

                default:
                    return "An unknown error occurred. Please verify your entry and try again. If the problem persists, please contact your system administrator.";
            }
        }
        #endregion
    }
}
