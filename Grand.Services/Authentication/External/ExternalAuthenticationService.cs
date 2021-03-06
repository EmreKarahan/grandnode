using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Grand.Core;
using Grand.Core.Data;
using Grand.Core.Domain.Customers;
using Grand.Core.Domain.Localization;
using Grand.Core.Plugins;
using Grand.Services.Common;
using Grand.Services.Customers;
using Grand.Services.Events;
using Grand.Services.Localization;
using Grand.Services.Logging;
using Grand.Services.Messages;
using Grand.Services.Orders;

namespace Grand.Services.Authentication.External
{
    /// <summary>
    /// Represents external authentication service implementation
    /// </summary>
    public partial class ExternalAuthenticationService : IExternalAuthenticationService
    {
        #region Fields

        private readonly CustomerSettings _customerSettings;
        private readonly ExternalAuthenticationSettings _externalAuthenticationSettings;
        private readonly IGrandAuthenticationService _authenticationService;
        private readonly ICustomerActivityService _customerActivityService;
        private readonly ICustomerRegistrationService _customerRegistrationService;
        private readonly ICustomerService _customerService;
        private readonly IEventPublisher _eventPublisher;
        private readonly IGenericAttributeService _genericAttributeService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILocalizationService _localizationService;
        private readonly IPluginFinder _pluginFinder;
        private readonly IRepository<ExternalAuthenticationRecord> _externalAuthenticationRecordRepository;
        private readonly IShoppingCartService _shoppingCartService;
        private readonly IStoreContext _storeContext;
        private readonly IWorkContext _workContext;
        private readonly IWorkflowMessageService _workflowMessageService;
        private readonly LocalizationSettings _localizationSettings;

        #endregion

        #region Ctor

        public ExternalAuthenticationService(CustomerSettings customerSettings,
            ExternalAuthenticationSettings externalAuthenticationSettings,
            IGrandAuthenticationService authenticationService,
            ICustomerActivityService customerActivityService,
            ICustomerRegistrationService customerRegistrationService,
            ICustomerService customerService,
            IEventPublisher eventPublisher,
            IGenericAttributeService genericAttributeService,
            IHttpContextAccessor httpContextAccessor,
            ILocalizationService localizationService,
            IPluginFinder pluginFinder,
            IRepository<ExternalAuthenticationRecord> externalAuthenticationRecordRepository,
            IShoppingCartService shoppingCartService,
            IStoreContext storeContext,
            IWorkContext workContext,
            IWorkflowMessageService workflowMessageService,
            LocalizationSettings localizationSettings)
        {
            this._customerSettings = customerSettings;
            this._externalAuthenticationSettings = externalAuthenticationSettings;
            this._authenticationService = authenticationService;
            this._customerActivityService = customerActivityService;
            this._customerRegistrationService = customerRegistrationService;
            this._customerService = customerService;
            this._eventPublisher = eventPublisher;
            this._genericAttributeService = genericAttributeService;
            this._httpContextAccessor = httpContextAccessor;
            this._localizationService = localizationService;
            this._pluginFinder = pluginFinder;
            this._externalAuthenticationRecordRepository = externalAuthenticationRecordRepository;
            this._shoppingCartService = shoppingCartService;
            this._storeContext = storeContext;
            this._workContext = workContext;
            this._workflowMessageService = workflowMessageService;
            this._localizationSettings = localizationSettings;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Authenticate user with existing associated external account
        /// </summary>
        /// <param name="associatedUser">Associated with passed external authentication parameters user</param>
        /// <param name="currentLoggedInUser">Current logged-in user</param>
        /// <param name="returnUrl">URL to which the user will return after authentication</param>
        /// <returns>Result of an authentication</returns>
        protected virtual IActionResult AuthenticateExistingUser(Customer associatedUser, Customer currentLoggedInUser, string returnUrl)
        {
            //log in guest user
            if (currentLoggedInUser == null)
                return LoginUser(associatedUser, returnUrl);

            //account is already assigned to another user
            if (currentLoggedInUser.Id != associatedUser.Id)
                //TODO create locale for error
                return Error(new[] { "Account is already assigned" }, returnUrl);

            if (String.IsNullOrEmpty(returnUrl))
                return new RedirectToRouteResult("HomePage", new { area = "" });
            return new RedirectResult(returnUrl);
        }

        /// <summary>
        /// Authenticate current user and associate new external account with user
        /// </summary>
        /// <param name="currentLoggedInUser">Current logged-in user</param>
        /// <param name="parameters">Authentication parameters received from external authentication method</param>
        /// <param name="returnUrl">URL to which the user will return after authentication</param>
        /// <returns>Result of an authentication</returns>
        protected virtual IActionResult AuthenticateNewUser(Customer currentLoggedInUser, ExternalAuthenticationParameters parameters, string returnUrl)
        {
            //associate external account with logged-in user
            if (currentLoggedInUser != null)
            {
                AssociateExternalAccountWithUser(currentLoggedInUser, parameters);
                if (String.IsNullOrEmpty(returnUrl))
                    return new RedirectToRouteResult("HomePage", new { area = "" });
                return new RedirectResult(returnUrl);
            }

            //or try to register new user
            if (_customerSettings.UserRegistrationType != UserRegistrationType.Disabled)
                return RegisterNewUser(parameters, returnUrl);

            //registration is disabled
            //TODO create locale for error
            return Error(new[] { "Registration is disabled" }, returnUrl);
        }

        /// <summary>
        /// Register new user
        /// </summary>
        /// <param name="parameters">Authentication parameters received from external authentication method</param>
        /// <param name="returnUrl">URL to which the user will return after authentication</param>
        /// <returns>Result of an authentication</returns>
        protected virtual IActionResult RegisterNewUser(ExternalAuthenticationParameters parameters, string returnUrl)
        {
            //if auto registration is disabled redirect to login page
            //TODO remove this setting
            if (!_externalAuthenticationSettings.AutoRegisterEnabled)
            {
                ExternalAuthorizerHelper.StoreParametersForRoundTrip(parameters);
                return new RedirectToActionResult("Login", "Customer", !string.IsNullOrEmpty(returnUrl) ? new { ReturnUrl = returnUrl } : null);
            }

            //or try to auto register new user
            //registration is approved if validation isn't required
            var registrationIsApproved = _customerSettings.UserRegistrationType == UserRegistrationType.Standard ||
                (_customerSettings.UserRegistrationType == UserRegistrationType.EmailValidation && !_externalAuthenticationSettings.RequireEmailValidation);

            //create registration request
            var registrationRequest = new CustomerRegistrationRequest(_workContext.CurrentCustomer,
                parameters.Email, parameters.Email,
                CommonHelper.GenerateRandomDigitCode(20),
                PasswordFormat.Clear,
                _storeContext.CurrentStore.Id,
                registrationIsApproved);

            //whether registration request has been completed successfully
            var registrationResult = _customerRegistrationService.RegisterCustomer(registrationRequest);
            if (!registrationResult.Success)
                return Error(registrationResult.Errors, returnUrl);

            //allow to save other customer values by consuming this event
            _eventPublisher.Publish(new CustomerAutoRegisteredByExternalMethodEvent(_workContext.CurrentCustomer, parameters));

            //raise vustomer registered event
            _eventPublisher.Publish(new CustomerRegisteredEvent(_workContext.CurrentCustomer));

            //store owner notifications
            if (_customerSettings.NotifyNewCustomerRegistration)
                _workflowMessageService.SendCustomerRegisteredNotificationMessage(_workContext.CurrentCustomer, _localizationSettings.DefaultAdminLanguageId);

            //associate external account with registered user
            AssociateExternalAccountWithUser(_workContext.CurrentCustomer, parameters);

            //authenticate
            if (registrationIsApproved)
            {
                _authenticationService.SignIn(_workContext.CurrentCustomer, false);
                _workflowMessageService.SendCustomerWelcomeMessage(_workContext.CurrentCustomer, _workContext.WorkingLanguage.Id);

                return new RedirectToRouteResult("RegisterResult", new { resultId = (int)UserRegistrationType.Standard });
            }

            //registration is succeeded but isn't activated
            if (_customerSettings.UserRegistrationType == UserRegistrationType.EmailValidation)
            {
                //email validation message
                _genericAttributeService.SaveAttribute(_workContext.CurrentCustomer, SystemCustomerAttributeNames.AccountActivationToken, Guid.NewGuid().ToString());
                _workflowMessageService.SendCustomerEmailValidationMessage(_workContext.CurrentCustomer, _workContext.WorkingLanguage.Id);

                return new RedirectToRouteResult("RegisterResult", new { resultId = (int)UserRegistrationType.EmailValidation });
            }

            //registration is succeeded but isn't approved by admin
            if (_customerSettings.UserRegistrationType == UserRegistrationType.AdminApproval)
                return new RedirectToRouteResult("RegisterResult", new { resultId = (int)UserRegistrationType.AdminApproval });

            //TODO create locale for error
            return Error(new[] { "Error on registration" }, returnUrl);
        }

        /// <summary>
        /// Login passed user
        /// </summary>
        /// <param name="user">User to login</param>
        /// <param name="returnUrl">URL to which the user will return after authentication</param>
        /// <returns>Result of an authentication</returns>
        protected virtual IActionResult LoginUser(Customer user, string returnUrl)
        {
            //migrate shopping cart
            _shoppingCartService.MigrateShoppingCart(_workContext.CurrentCustomer, user, true);

            //authenticate
            _authenticationService.SignIn(user, false);

            //raise event       
            _eventPublisher.Publish(new CustomerLoggedinEvent(user));

            // activity log
            _customerActivityService.InsertActivity("PublicStore.Login", "", _localizationService.GetResource("ActivityLog.PublicStore.Login"), user);

            if (String.IsNullOrEmpty(returnUrl))
                return new RedirectToRouteResult("HomePage", new { area = "" });
            return new RedirectResult(returnUrl);
        }

        /// <summary>
        /// Add errors that occurred during authentication
        /// </summary>
        /// <param name="errors">Collection of errors</param>
        /// <param name="returnUrl">URL to which the user will return after authentication</param>
        /// <returns>Result of an authentication</returns>
        protected virtual IActionResult Error(IEnumerable<string> errors, string returnUrl)
        {
            foreach (var error in errors)
                ExternalAuthorizerHelper.AddErrorsToDisplay(error);

            return new RedirectToActionResult("Login", "Customer", !string.IsNullOrEmpty(returnUrl) ? new { ReturnUrl = returnUrl } : null);
        }

        #endregion

        #region Methods

        #region External authentication methods

        /// <summary>
        /// Load active external authentication methods
        /// </summary>
        /// <param name="customer">Load records allowed only to a specified customer; pass null to ignore ACL permissions</param>
        /// <param name="storeId">Load records allowed only in a specified store; pass 0 to load all records</param>
        /// <returns>Payment methods</returns>
        public virtual IList<IExternalAuthenticationMethod> LoadActiveExternalAuthenticationMethods(Customer customer = null, string storeId = "")
        {
            return LoadAllExternalAuthenticationMethods(customer, storeId)
                .Where(provider => _externalAuthenticationSettings.ActiveAuthenticationMethodSystemNames
                    .Contains(provider.PluginDescriptor.SystemName, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Load external authentication method by system name
        /// </summary>
        /// <param name="systemName">System name</param>
        /// <returns>Found external authentication method</returns>
        public virtual IExternalAuthenticationMethod LoadExternalAuthenticationMethodBySystemName(string systemName)
        {
            var descriptor = _pluginFinder.GetPluginDescriptorBySystemName<IExternalAuthenticationMethod>(systemName);
            if (descriptor != null)
                return descriptor.Instance<IExternalAuthenticationMethod>();

            return null;
        }

        /// <summary>
        /// Load all external authentication methods
        /// </summary>
        /// <param name="customer">Load records allowed only to a specified customer; pass null to ignore ACL permissions</param>
        /// <param name="storeId">Load records allowed only in a specified store; pass 0 to load all records</param>
        /// <returns>External authentication methods</returns>
        public virtual IList<IExternalAuthenticationMethod> LoadAllExternalAuthenticationMethods(Customer customer = null, string storeId = "")
        {
            return _pluginFinder.GetPlugins<IExternalAuthenticationMethod>().ToList();
        }

        /// <summary>
        /// Check whether authentication by the passed external authentication method is available
        /// </summary>
        /// <param name="systemName">System name of the external authentication method</param>
        /// <returns>True if authentication is available; otherwise false</returns>
        public virtual bool ExternalAuthenticationMethodIsAvailable(string systemName)
        {
            //load method
            var authenticationMethod = LoadExternalAuthenticationMethodBySystemName(systemName);

            return authenticationMethod != null &&
                authenticationMethod.IsMethodActive(_externalAuthenticationSettings) &&
                authenticationMethod.PluginDescriptor.Installed &&
                _pluginFinder.AuthenticateStore(authenticationMethod.PluginDescriptor, _storeContext.CurrentStore.Id);
        }

        #endregion

        #region Authentication

        /// <summary>
        /// Authenticate user by passed parameters
        /// </summary>
        /// <param name="parameters">External authentication parameters</param>
        /// <param name="returnUrl">URL to which the user will return after authentication</param>
        /// <returns>Result of an authentication</returns>
        public virtual IActionResult Authenticate(ExternalAuthenticationParameters parameters, string returnUrl = null)
        {
            if (parameters == null)
                throw new ArgumentNullException("parameters");

            if (!ExternalAuthenticationMethodIsAvailable(parameters.ProviderSystemName))
                //TODO create locale for error
                return Error(new[] { "External authentication method cannot be loaded" }, returnUrl);

            //get current logged-in user
            var currentLoggedInUser = _workContext.CurrentCustomer.IsRegistered() ? _workContext.CurrentCustomer : null;

            //authenticate associated user if already exists
            var associatedUser = GetUserByExternalAuthenticationParameters(parameters);
            if (associatedUser != null)
                return AuthenticateExistingUser(associatedUser, currentLoggedInUser, returnUrl);

            //or associate and authenticate new user
            return AuthenticateNewUser(currentLoggedInUser, parameters, returnUrl);
        }

        #endregion

        /// <summary>
        /// Accociate external account with customer
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="parameters">External authentication parameters</param>
        public virtual void AssociateExternalAccountWithUser(Customer customer, ExternalAuthenticationParameters parameters)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");

            var externalAuthenticationRecord = new ExternalAuthenticationRecord
            {
                CustomerId = customer.Id,
                Email = parameters.Email,
                ExternalIdentifier = parameters.ExternalIdentifier,
                ExternalDisplayIdentifier = parameters.ExternalDisplayIdentifier,
                OAuthAccessToken = parameters.AccessToken,
                ProviderSystemName = parameters.ProviderSystemName,
            };

            _externalAuthenticationRecordRepository.Insert(externalAuthenticationRecord);
        }

        /// <summary>
        /// Get the particular user with specified parameters
        /// </summary>
        /// <param name="parameters">External authentication parameters</param>
        /// <returns>Customer</returns>
        public virtual Customer GetUserByExternalAuthenticationParameters(ExternalAuthenticationParameters parameters)
        {
            if (parameters == null)
                throw new ArgumentNullException("parameters");

            var associationRecord = _externalAuthenticationRecordRepository.Table.FirstOrDefault(record =>
                record.ExternalIdentifier.Equals(parameters.ExternalIdentifier) && record.ProviderSystemName.Equals(parameters.ProviderSystemName));
            if (associationRecord == null)
                return null;

            return _customerService.GetCustomerById(associationRecord.CustomerId);
        }

        /// <summary>
        /// Remove the association
        /// </summary>
        /// <param name="parameters">External authentication parameters</param>
        public virtual void RemoveAssociation(ExternalAuthenticationParameters parameters)
        {
            if (parameters == null)
                throw new ArgumentNullException("parameters");

            var associationRecord = _externalAuthenticationRecordRepository.Table.FirstOrDefault(record =>
                record.ExternalIdentifier.Equals(parameters.ExternalIdentifier) && record.ProviderSystemName.Equals(parameters.ProviderSystemName));

            if (associationRecord != null)
                _externalAuthenticationRecordRepository.Delete(associationRecord);
        }

        public virtual IList<ExternalAuthenticationRecord> GetExternalIdentifiersFor(Customer customer)
        {
            if (customer == null)
                throw new ArgumentNullException("customer");
            var query = from p in _externalAuthenticationRecordRepository.Table
                        where p.CustomerId == customer.Id
                        select p;
            return query.ToList(); 
        }

        /// <summary>
        /// Delete the external authentication record
        /// </summary>
        /// <param name="externalAuthenticationRecord">External authentication record</param>
        public virtual void DeleteExternalAuthenticationRecord(ExternalAuthenticationRecord externalAuthenticationRecord)
        {
            if (externalAuthenticationRecord == null)
                throw new ArgumentNullException("externalAuthenticationRecord");

            _externalAuthenticationRecordRepository.Delete(externalAuthenticationRecord);
        }

        #endregion
    }
}