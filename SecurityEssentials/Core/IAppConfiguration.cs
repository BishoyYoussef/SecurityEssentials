﻿namespace SecurityEssentials.Core
{
    public interface IAppConfiguration
    {
        bool AccountManagementRegisterAutoApprove { get; }
        string ApplicationName { get; }
        string DefaultFromEmailAddress { get; }
        int EncryptionIterationCount { get; }
        string EncryptionPassword { get; }
		bool HasEmailConfigured { get; }
        bool HasRecaptcha { get; }
        string WebsiteBaseUrl { get; }
    }
}