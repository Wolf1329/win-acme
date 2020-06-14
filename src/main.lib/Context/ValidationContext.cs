﻿using ACMESharp.Authorizations;
using ACMESharp.Protocol.Resources;
using Autofac;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Plugins.Interfaces;
using System.Collections.Generic;

namespace PKISharp.WACS.Context
{
    public class ValidationContextParameters
    {
        public ValidationContextParameters(
            Authorization authorization,
            TargetPart? targetPart,
            string challengeType,
            string pluginName)
        {
            TargetPart = targetPart;
            Authorization = authorization;
            ChallengeType = challengeType;
            PluginName = pluginName;
        }

        public string ChallengeType { get; }
        public string PluginName { get; }
        public TargetPart? TargetPart { get; }
        public Authorization Authorization { get; }
    }

    public class ValidationContext
    {
        public ValidationContext(
            ILifetimeScope scope,
            ValidationContextParameters parameters)
        {
            Identifier = parameters.Authorization.Identifier.Value;
            TargetPart = parameters.TargetPart;
            Authorization = parameters.Authorization;
            Scope = scope;
            ChallengeType = parameters.ChallengeType;
            PluginName = parameters.PluginName;
            ValidationPlugin = scope.Resolve<IValidationPlugin>();
        }
        public ILifetimeScope Scope { get; }
        public string Identifier { get; }
        public string ChallengeType { get; }
        public string PluginName { get; }
        public TargetPart? TargetPart { get; }
        public Authorization Authorization { get; }
        public Challenge? Challenge { get; set; }
        public IChallengeValidationDetails? ChallengeDetails { get; set; }
        public IValidationPlugin ValidationPlugin { get; set; }
        public bool? Success { get; set; }
        public List<string> ErrorMessages { get; } = new List<string>();
        public void AddErrorMessage(string? value, bool fatal = true)
        {
            if (value != null)
            {
                if (!ErrorMessages.Contains(value))
                {
                    ErrorMessages.Add(value);
                }
            }
            if (fatal)
            {
                Success = false;
            }
        }
    }

}
