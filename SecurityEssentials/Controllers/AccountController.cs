﻿using Microsoft.AspNet.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Threading.Tasks;
using System.Web.Mvc;
using SecurityEssentials.Model;
using SecurityEssentials.Core.Identity;
using SecurityEssentials.Core;
using SecurityEssentials.ViewModel;
using System.Configuration;
using System.Web.Security;

namespace SecurityEssentials.Controllers
{
    public class AccountController : AntiForgeryControllerBase
    {

        private IAppConfiguration _configuration;
        private IEncryption _encryption;
        private IFormsAuth _formsAuth;
        private IRecaptcha _recaptcha;
        private IServices _services;
        private ISEContext _context;
        private IUserManager _userManager;
        private IUserIdentity _userIdentity;

        public AccountController()
            : this(new AppConfiguration(), new Encryption(), new FormsAuth(), new SEContext(), new AppUserManager(), new SecurityCheckRecaptcha(), new Services(), new UserIdentity())
        {
            // TODO: Replace with your DI Framework of choice
        }

        public AccountController(IAppConfiguration configuration, IEncryption encryption, IFormsAuth formsAuth, ISEContext context, IUserManager userManager, IRecaptcha recaptcha, IServices services, IUserIdentity userIdentity)
        {
            if (configuration == null) throw new ArgumentNullException("configuration");
            if (context == null) throw new ArgumentNullException("context");
            if (encryption == null) throw new ArgumentNullException("encryption");
            if (formsAuth == null) throw new ArgumentNullException("formsAuth");
            if (recaptcha == null) throw new ArgumentNullException("recaptcha");
            if (services == null) throw new ArgumentNullException("services");
            if (userManager == null) throw new ArgumentNullException("userManager");
            if (userIdentity == null) throw new ArgumentNullException("userIdentity");

            _configuration = configuration;
            _context = context;
            _encryption = encryption;
            _formsAuth = formsAuth;
            _recaptcha = recaptcha;
            _services = services;
            _userManager = userManager;
            _userIdentity = userIdentity;
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public ActionResult LogOff()
        {
            _formsAuth.SignOut();
            _userManager.SignOut();
            Session.Abandon();
            return RedirectToAction("LogOn", "Account");
        }

        [AllowAnonymous]
        public ActionResult LogOn(string returnUrl)
        {
            if (Request.IsAuthenticated)
            {
                return RedirectToAction("Landing", "Account");
            }
            ViewBag.ReturnUrl = returnUrl;
            return View("LogOn");
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [AllowXRequestsEveryXSecondsAttribute(Name = "LogOn", Message = "You have performed this action more than {x} times in the last {n} seconds.", Requests = 3, Seconds = 60)]
        public async Task<ActionResult> LogOn(LogOn model, string returnUrl)
        {
            if (ModelState.IsValid)
            {
                var user = await _userManager.TrySignInAsync(model.UserName, model.Password);
                if (user.Success)
                {
                    await _userManager.SignInAsync(user.UserName, model.RememberMe);
                    return RedirectToLocal(returnUrl);
                }
                else
                {
                    // SECURE: Increasing wait time (with random component) for each successive logon failure (instead of locking out)
                    _services.Wait(500 + (user.FailedLogonAttemptCount * 200) + (new Random().Next(4) * 200));
                    ModelState.AddModelError("", "Invalid credentials or the account is locked");
                }
            }

            // If we got this far, something failed, redisplay form
            return View(model);
        }

		/// <summary>
		/// 
		/// </summary>
		/// <param name="id">Unique identifier for the user</param>
		/// <returns></returns>
		public ActionResult ChangeEmailAddress()
		{
			var userId = _userIdentity.GetUserId(this);
			var users = _context.User.Where(u => u.Id == userId);
			if (users.ToList().Count == 0) return new HttpNotFoundResult();
			var user = users.FirstOrDefault();
			// SECURE: Check user should have access to this account
			if (!_userIdentity.IsUserInRole(this, "Admin") && _userIdentity.GetUserId(this) != user.Id) return new HttpNotFoundResult();
			return View(new ChangeEmailAddressViewModel(user.UserName, user.NewUserName, user.NewUserNameRequestExpiryDate));
		}

		public ActionResult ChangePassword(ManageMessageId? message)
        {
            ViewBag.StatusMessage =
                message == ManageMessageId.ChangePasswordSuccess ? "Your password has been changed."
                : message == ManageMessageId.Error ? "An error has occurred."
                : "";
            ViewBag.ReturnUrl = Url.Action("ChangePassword");
            var model = new ChangePasswordViewModel()
            {
                HasRecaptcha = _configuration.HasRecaptcha
            };
            return View(model);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        [AllowXRequestsEveryXSecondsAttribute(Name = "ChangePassword", Message = "You have performed this action more than {x} times in the last {n} seconds.", Requests = 2, Seconds = 60)]
        public async Task<ActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            ViewBag.ReturnUrl = Url.Action("ChangePassword");
            var userId = _userIdentity.GetUserId(this);
            var recaptchaSuccess = true;
            if (_configuration.HasRecaptcha)
            {
                _recaptcha.ValidateRecaptcha(this);
            }
            if (recaptchaSuccess)
            {
                var user = _context.User.Where(u => u.Id == userId).FirstOrDefault();
                if (user != null)
                {
                    var result = await _userManager.ChangePasswordAsync(userId, model.OldPassword, model.NewPassword);
                    if (result.Succeeded)
                    {
                        // Email recipient with password change acknowledgement
                        string emailBody = string.Format("Just a little note from {0} to say your password has been changed today, if this wasn't done by yourself, please contact the site administrator asap", _configuration.ApplicationName);
                        string emailSubject = string.Format("{0} - Password change confirmation", _configuration.ApplicationName);
                        _services.SendEmail(_configuration.DefaultFromEmailAddress, new List<string>() { user.UserName }, null, null, emailSubject, emailBody, true);
                        _context.SaveChanges();
                        return RedirectToAction("ChangePassword", new { Message = ManageMessageId.ChangePasswordSuccess });
                    }
                    else
                    {
                        AddErrors(result);
                    }
                }
                else
                {
                    return HttpNotFound();
                }
            }
            return View(model);
        }

        [AllowAnonymous]
        [AllowXRequestsEveryXSecondsAttribute(Name = "EmailVerify", ContentName = "TooManyRequests", Requests = 2, Seconds = 60)]
        public async Task<ActionResult> EmailVerify()
        {
            var emailVerificationToken = Request.QueryString["EmailVerficationToken"] ?? "";
            var user = _context.User.Where(u => u.EmailConfirmationToken == emailVerificationToken).FirstOrDefault();
            if (user == null)
            {
                HandleErrorInfo error = new HandleErrorInfo(new ArgumentException("INFO: The email verification token is not valid or has expired"), "Account", "EmailVerify");
                return View("Error", error);
            }
            user.EmailVerified = true;
            user.EmailConfirmationToken = null;
            user.UserLogs.Add(new UserLog() { Description = "User Confirmed Email Address" });
            await _context.SaveChangesAsync();
            return View("EmailVerificationSuccess");
        }

        [AllowAnonymous]
        public ActionResult Recover()
        {
            var model = new RecoverViewModel()
            {
                HasRecaptcha = _configuration.HasRecaptcha
            };
            return View("Recover", model);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [AllowXRequestsEveryXSecondsAttribute(Name = "Recover", ContentName = "TooManyRequests", Requests = 2, Seconds = 60)]
        public async Task<ActionResult> Recover(RecoverViewModel model)
        {
            if (ModelState.IsValid)
            {
                var user = _context.User.Where(u => u.UserName == model.UserName && u.Enabled && u.EmailVerified && u.Approved).FirstOrDefault();
                var recaptchaSuccess = true;
                if (_configuration.HasRecaptcha)
                {
                    _recaptcha.ValidateRecaptcha(this);
                    if (!recaptchaSuccess)
                    {
                        return View(model);
                    }
                }
                if (user != null)
                {
                    user.PasswordResetToken = Guid.NewGuid().ToString().Replace("-", "");
                    user.PasswordResetExpiry = DateTime.UtcNow.AddMinutes(15);
                    // Send recovery email with link to recover password form
                    string emailBody = string.Format("A request has been received to reset your {0} password. You can complete this process any time within the next 15 minutes by clicking <a href='{1}Account/RecoverPassword?PasswordResetToken={2}'>{1}Account/RecoverPassword?PasswordResetToken={2}</a>. If you did not request this then you can ignore this email.",
                        _configuration.ApplicationName, _configuration.WebsiteBaseUrl, user.PasswordResetToken);
                    string emailSubject = string.Format("{0} - Complete the password recovery process", _configuration.ApplicationName);
                    _services.SendEmail(_configuration.DefaultFromEmailAddress, new List<string>() { user.UserName }, null, null, emailSubject, emailBody, true);
                    user.UserLogs.Add(new UserLog() { Description = "Password reset link generated and sent" });
                    _context.SaveChanges();
                }
            }
            return View("RecoverSuccess");

        }

        [AllowAnonymous]
        public async Task<ActionResult> RecoverPassword()
        {
            var passwordResetToken = Request.QueryString["PasswordResetToken"] ?? "";
            var user = _context.User.Include("SecurityQuestionLookupItem").Where(u => u.PasswordResetToken == passwordResetToken && u.PasswordResetExpiry > DateTime.UtcNow).FirstOrDefault();
            if (user == null)
            {
                HandleErrorInfo error = new HandleErrorInfo(new ArgumentException("INFO: The password recovery token is not valid or has expired"), "Account", "RecoverPassword");
                return View("Error", error);
            }
            if (user.Enabled == false)
            {
                HandleErrorInfo error = new HandleErrorInfo(new InvalidOperationException("INFO: Your account is not currently approved or active"), "Account", "Recover");
                return View("Error", error);
            }
            RecoverPasswordViewModel recoverPasswordModel = new RecoverPasswordViewModel()
            {
                Id = user.Id,
                HasRecaptcha = _configuration.HasRecaptcha,
                SecurityAnswer = "",
                SecurityQuestion = user.SecurityQuestionLookupItem.Description,
                PasswordResetToken = passwordResetToken,
                UserName = user.UserName
            };
            return View("RecoverPassword", recoverPasswordModel);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [AllowXRequestsEveryXSecondsAttribute(Name = "RecoverPassword", ContentName = "TooManyRequests", Requests = 2, Seconds = 60)]
        public async Task<ActionResult> RecoverPassword(RecoverPasswordViewModel recoverPasswordModel)
        {
            var user = _context.User.Where(u => u.Id == recoverPasswordModel.Id).FirstOrDefault();
            if (user == null)
            {
                HandleErrorInfo error = new HandleErrorInfo(new Exception("INFO: The user is not valid"), "Account", "RecoverPassword");
                return View("Error", error);
            }
            if (!(user.Enabled))
            {
                HandleErrorInfo error = new HandleErrorInfo(new Exception("INFO: Your account is not currently approved or active"), "Account", "Recover");
                return View("Error", error);
            }
            string encryptedSecurityAnswer = "";
            _encryption.Encrypt(_configuration.EncryptionPassword, user.Salt,
                    _configuration.EncryptionIterationCount, recoverPasswordModel.SecurityAnswer, out encryptedSecurityAnswer);
            if (user.SecurityAnswer != encryptedSecurityAnswer)
            {
                ModelState.AddModelError("SecurityAnswer", "The security answer is incorrect");
                return View("RecoverPassword", recoverPasswordModel);
            }
            if (recoverPasswordModel.Password != recoverPasswordModel.ConfirmPassword)
            {
                ModelState.AddModelError("ConfirmPassword", "The passwords do not match");
                return View("RecoverPassword", recoverPasswordModel);
            }
            var recaptchaSuccess = true;
            if (_configuration.HasRecaptcha)
            {
                _recaptcha.ValidateRecaptcha(this);
            }
            if (ModelState.IsValid && recaptchaSuccess)
            {
                var result = await _userManager.ChangePasswordFromTokenAsync(user.Id, recoverPasswordModel.PasswordResetToken, recoverPasswordModel.Password);
                if (result.Succeeded)
                {
                    _context.SaveChanges();
                    await _userManager.SignInAsync(user.UserName, false);
                    return View("RecoverPasswordSuccess");
                }
                else
                {
                    AddErrors(result);
                    return View("RecoverPassword", recoverPasswordModel);
                }
            }
            else
            {
                ModelState.AddModelError("", "Password change was not successful");
                return View("RecoverPassword", recoverPasswordModel);
            }

        }

        [Authorize]
        public async Task<ActionResult> ChangeSecurityInformation()
        {
            var securityQuestions = _context.LookupItem.Where(l => l.LookupTypeId == CONSTS.LookupTypeId.SecurityQuestion && l.IsHidden == false).OrderBy(o => o.Ordinal).ToList();
            var changeSecurityInformationViewModel = new ChangeSecurityInformationViewModel("", _configuration.HasRecaptcha, securityQuestions);
            return View(changeSecurityInformationViewModel);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [AllowXRequestsEveryXSecondsAttribute(Name = "ChangeSecurityInformation", ContentName = "TooManyRequests", Requests = 2, Seconds = 60)]
        public async Task<ActionResult> ChangeSecurityInformation(ChangeSecurityInformationViewModel model)
        {
            string errorMessage = "";

            if (ModelState.IsValid)
            {
                var recaptchaSuccess = true;
                if (_configuration.HasRecaptcha)
                {
                    _recaptcha.ValidateRecaptcha(this);
                }
                var logonResult = await _userManager.TrySignInAsync(_userIdentity.GetUserName(this), model.Password);
                if (recaptchaSuccess && logonResult.Success)
                {
                    if (model.SecurityAnswer == model.SecurityAnswerConfirm)
                    {
                        var user = _context.User.Where(u => u.UserName == logonResult.UserName).FirstOrDefault();
                        string encryptedSecurityAnswer = "";
                        _encryption.Encrypt(_configuration.EncryptionPassword, user.Salt,
                                _configuration.EncryptionIterationCount, model.SecurityAnswer, out encryptedSecurityAnswer);
                        user.SecurityAnswer = encryptedSecurityAnswer;
                        user.SecurityQuestionLookupItemId = model.SecurityQuestionLookupItemId;
                        user.UserLogs.Add(new UserLog() { Description = "User Changed Security Information" });
                        await _context.SaveChangesAsync();

                        // Email the user to complete the email verification process or inform them of a duplicate registration and would they like to change their password
                        string emailBody = "";
                        string emailSubject = "";
                        emailSubject = string.Format("{0} - Security Information Changed", _configuration.ApplicationName);
                        emailBody = string.Format("Hello {0}, please be advised that the security information on your account for {1} been changed. If you did not initiate this action then please contact the site administrator as soon as possible", user.FullName, _configuration.ApplicationName);
                        _services.SendEmail(_configuration.DefaultFromEmailAddress, new List<string>() { logonResult.UserName }, null, null, emailSubject, emailBody, true);
                        return View("ChangeSecurityInformationSuccess");
                    }
                    else
                    {
                        errorMessage = "The security question answers do not match";
                    }
                }
                else
                {
                    errorMessage = "Security information incorrect or account locked out";
                }
            }
            var securityQuestions = _context.LookupItem.Where(l => l.LookupTypeId == CONSTS.LookupTypeId.SecurityQuestion && l.IsHidden == false).OrderBy(o => o.Ordinal).ToList();
            var changeSecurityInformationViewModel = new ChangeSecurityInformationViewModel(errorMessage, _configuration.HasRecaptcha, securityQuestions);
            return View(changeSecurityInformationViewModel);

        }

        [AllowAnonymous]
        public async Task<ActionResult> Register()
        {
            var securityQuestions = _context.LookupItem.Where(l => l.LookupTypeId == CONSTS.LookupTypeId.SecurityQuestion && l.IsHidden == false).OrderBy(o => o.Ordinal).ToList();
            var registerViewModel = new RegisterViewModel("", _configuration.HasRecaptcha, "", new User(), securityQuestions);
            return View(registerViewModel);
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        [AllowXRequestsEveryXSecondsAttribute(Name = "Register", ContentName = "TooManyRequests", Requests = 2, Seconds = 60)]
        public async Task<ActionResult> Register(FormCollection collection)
        {
            var user = new User();
            var password = collection["Password"].ToString();
            var confirmPassword = collection["ConfirmPassword"].ToString();

            if (ModelState.IsValid)
            {
                var propertiesToUpdate = new[]
            {
                    "FirstName", "LastName", "UserName", "SecurityQuestionLookupItemId", "SecurityAnswer"
                };
                if (TryUpdateModel(user, "User", propertiesToUpdate, collection))
                {
                    var recaptchaSuccess = true;
                    if (_configuration.HasRecaptcha)
                    {
                        _recaptcha.ValidateRecaptcha(this);
                    }
                    if (recaptchaSuccess)
                    {
                        var result = await _userManager.CreateAsync(user.UserName, user.FirstName, user.LastName, password, confirmPassword,
                            user.SecurityQuestionLookupItemId, user.SecurityAnswer);
                        if (result.Succeeded || result.Errors.Any(e => e == "Username already registered"))
                        {
                            user = _context.User.Where(u => u.UserName == user.UserName).First();
                            // Email the user to complete the email verification process or inform them of a duplicate registration and would they like to change their password
                            string emailBody = "";
                            string emailSubject = "";
                            if (result.Succeeded)
                            {
                                emailSubject = string.Format("{0} - Complete your registration", _configuration.ApplicationName);
                                emailBody = string.Format("Welcome to {0}, to complete your registration we just need to confirm your email address by clicking <a href='{1}Account/EmailVerify?EmailVerficationToken={2}'>{1}Account/EmailVerify?EmailVerficationToken={2}</a>. If you did not request this registration then you can ignore this email and do not need to take any further action", _configuration.ApplicationName, _configuration.WebsiteBaseUrl, user.EmailConfirmationToken);
                            }
                            else
                            {
                                emailSubject = string.Format("{0} - Duplicate Registration", _configuration.ApplicationName);
                                emailBody = string.Format("You already have an account on {0}. You (or possibly someone else) just attempted to register on {0} with this email address. However you are registered and cannot re-register with the same address. If you'd like to login you can do so by clicking here: <a href='{1}Account/LogOn'>{1}Account/LogOn</a>. If you have forgotten your password you can answer some security questions here to reset your password:<a href='{1}Account/LogOn'>{1}Account/Recover</a>. If it wasn't you who attempted to register with this email address or you did it by mistake, you can safely ignore this email", _configuration.ApplicationName, _configuration.WebsiteBaseUrl);
                            }

                            _services.SendEmail(_configuration.DefaultFromEmailAddress, new List<string>() { user.UserName }, null, null, emailSubject, emailBody, true);
                            return View("RegisterSuccess");
                        }
                        else
                        {
                            AddErrors(result);
                        }
                    }
                }
            }
            var securityQuestions = _context.LookupItem.Where(l => l.LookupTypeId == CONSTS.LookupTypeId.SecurityQuestion && l.IsHidden == false).OrderBy(o => o.Ordinal).ToList();
            var registerViewModel = new RegisterViewModel(confirmPassword, _configuration.HasRecaptcha, password, user, securityQuestions);
            return View(registerViewModel);

        }

        public ActionResult Landing()
        {
            var currentUserId = _userIdentity.GetUserId(this);
            var users = _context.User.Where(u => u.Id == currentUserId);
            if (users.ToList().Count == 0) return new HttpNotFoundResult();
            var user = users.FirstOrDefault();
            var activityLogs = user.UserLogs.OrderByDescending(d => d.DateCreated);
            UserLog lastAccountActivity = null;
            if (activityLogs.ToList().Count > 1)
            {
                lastAccountActivity = activityLogs.Skip(1).FirstOrDefault();
            }
            return View(new LandingViewModel(user.FirstName, lastAccountActivity, currentUserId));
        }

        #region Helper Functions

        private void AddErrors(SEIdentityResult result)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error);
            }
        }

        private ActionResult RedirectToLocal(string returnUrl)
        {
            if (Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            else
            {
                return RedirectToAction("Landing", "Account");
            }
        }

        public enum ManageMessageId
        {
            ChangePasswordSuccess,
            Error
        }

        #endregion

    }
}