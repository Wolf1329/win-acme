﻿using Autofac;
using PKISharp.WACS.Clients.Acme;
using PKISharp.WACS.Context;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Plugins.Base.Options;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PKISharp.WACS
{
    /// <summary>
    /// This part of the code handles the actual creation/renewal of ACME certificates
    /// </summary>
    internal class RenewalValidator
    {
        private readonly IAutofacBuilder _scopeBuilder;
        private readonly ILogService _log;
        private readonly ISettingsService _settings;
        private readonly ExceptionHandler _exceptionHandler;
        public RenewalValidator(IAutofacBuilder scopeBuilder, ISettingsService settings, ILogService log, ExceptionHandler exceptionHandler)
        {
            _scopeBuilder = scopeBuilder;
            _log = log;
            _exceptionHandler = exceptionHandler;
            _settings = settings;
        }

        /// <summary>
        /// Answer all the challenges in the order
        /// </summary>
        /// <param name="execute"></param>
        /// <param name="order"></param>
        /// <param name="result"></param>
        /// <param name="runLevel"></param>
        /// <returns></returns>
        public async Task AuthorizeOrder(ExecutionContext context)
        {
            // Sanity check
            if (context.Order.Details == null)
            {
                context.Result.AddErrorMessage($"Unable to create order");
                return;
            }

            // Get validation plugin
            var options = context.Renewal.ValidationPluginOptions;
            var validationScope = _scopeBuilder.Validation(context.Scope, options);
            var validationPlugin = validationScope.Resolve<IValidationPlugin>();
            if (validationPlugin == null)
            {
                _log.Error("Validation plugin not found or not created");
                context.Result.AddErrorMessage("Validation plugin not found or not created");
                return;
            }
            var (disabled, disabledReason) = validationPlugin.Disabled;
            if (disabled)
            {
                _log.Error($"Validation plugin is not available. {disabledReason}");
                context.Result.AddErrorMessage("Validation plugin is not available");
                return;
            }

            // Get authorization details
            var authorizations = context.Order.Details.Payload.Authorizations.ToList();
            var contextParamTasks = authorizations.Select(authorizationUri => GetValidationContextParameters(context, authorizationUri, options));
            var contextParams = (await Task.WhenAll(contextParamTasks)).ToList();
            var missingTarget = contextParams.FirstOrDefault(x => x.TargetPart == null);
            if (missingTarget != null)
            {
                context.Result.AddErrorMessage($"Unable to match challenge {missingTarget.Authorization.Identifier.Value} to target");
                return;
            }

            if (_settings.Validation.DisableMultiThreading == true || 
                validationPlugin.Parallelism == ParallelOperations.None)
            {
                await SerialValidation(context, contextParams);
            }
            else
            {
                await ParallelValidation(validationPlugin.Parallelism, validationScope, context, contextParams);
            }
        }

        /// <summary>
        /// Handle multiple validations in parallel 
        /// </summary>
        /// <returns></returns>
        private async Task ParallelValidation(ParallelOperations level, ILifetimeScope scope, ExecutionContext context, List<ValidationContextParameters> parameters)
        {
            var contexts = parameters.Select(parameter => new ValidationContext(scope, parameter)).ToList();
            
            // Prepare for challenge answer
            if (level.HasFlag(ParallelOperations.Prepare))
            {
                // Parallel
                _log.Verbose("Handle {n} preparation(s)", contexts.Count);
                var prepareTasks = contexts.Select(vc => PrepareChallengeAnswer(vc, context.RunLevel));
                await Task.WhenAll(prepareTasks);
                foreach (var ctx in contexts)
                {
                    TransferErrors(ctx, context.Result);
                }
                if (!context.Result.Success)
                {
                    return;
                }
            }
            else
            {
                // Serial
                foreach (var ctx in contexts)
                {
                    await PrepareChallengeAnswer(ctx, context.RunLevel);
                    TransferErrors(ctx, context.Result);
                    if (!context.Result.Success)
                    {
                        return;
                    }
                }
            }

            // Submit challenge answer
            if (level.HasFlag(ParallelOperations.Answer))
            {
                // Parallel
                _log.Verbose("Handle {n} answers(s)", contexts.Count);
                var answerTasks = contexts.Select(vc => AnswerChallenge(vc));
                await Task.WhenAll(answerTasks);
                foreach (var ctx in contexts)
                {
                    TransferErrors(ctx, context.Result);
                }
                if (!context.Result.Success)
                {
                    return;
                }
            }
            else
            {
                // Serial
                foreach (var ctx in contexts)
                {
                    await AnswerChallenge(ctx);
                    TransferErrors(ctx, context.Result);
                    if (!context.Result.Success)
                    {
                        return;
                    }
                }
            }

            if (level.HasFlag(ParallelOperations.Clean))
            {
                // Parallel
                _log.Verbose("Handle {n} cleanups(s)", contexts.Count);
                var cleanUpTasks = contexts.Select(vc => CleanValidation(vc));
                await Task.WhenAll(cleanUpTasks);
                foreach (var ctx in contexts)
                {
                    TransferErrors(ctx, context.Result);
                }
            }
            else
            {
                // Serial
                foreach (var ctx in contexts)
                {
                    if (ctx.Challenge != null)
                    {
                        // Cleanup
                        await CleanValidation(ctx);
                        TransferErrors(ctx, context.Result);
                    }
                }
            }
        }

        /// <summary>
        /// Handle validation in serial order
        /// </summary>
        /// <param name="context"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        private async Task SerialValidation(ExecutionContext context, List<ValidationContextParameters> parameters)
        {
            foreach (var parameter in parameters)
            {
                _log.Verbose("Handle authorization {n}/{m}",
                    parameters.IndexOf(parameter) + 1, 
                    parameters.Count);
                using var identifierScope = _scopeBuilder.Validation(context.Scope, context.Renewal.ValidationPluginOptions);
                await ParallelValidation(ParallelOperations.None, identifierScope, context, new List<ValidationContextParameters> { parameter });
                if (!context.Result.Success)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Get information needed to construct a validation context (shared between serial and parallel mode)
        /// </summary>
        /// <param name="context"></param>
        /// <param name="authorizationUri"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        private async Task<ValidationContextParameters> GetValidationContextParameters(ExecutionContext context, string authorizationUri, ValidationPluginOptions options)
        {
            // Get authorization challenge details from server
            var client = context.Scope.Resolve<AcmeClient>();
            var authorization = await client.GetAuthorizationDetails(authorizationUri);

            // Find a targetPart that matches the challenge
            var targetPart = context.Target.Parts.
                FirstOrDefault(tp => tp.GetHosts(false).
                Any(h => authorization.Identifier.Value == h.Replace("*.", "")));

            return new ValidationContextParameters(authorization, targetPart, options.ChallengeType, options.Name);
        }

        /// <summary>
        /// Move errors from a validation context up to the renewal result
        /// </summary>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="prefix"></param>
        private void TransferErrors(ValidationContext from, RenewResult to)
        {
            from.ErrorMessages.ForEach(e => to.AddErrorMessage($"[{from.Identifier}] {e}", from.Success == false));
            from.ErrorMessages.Clear();
        }

  
        /// <summary>
        /// Make sure we have authorization for every host in target
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        private async Task PrepareChallengeAnswer(ValidationContext context, RunLevel runLevel)
        {
            if (context.ValidationPlugin == null)
            {
                throw new InvalidOperationException();
            }
            var client = context.Scope.Resolve<AcmeClient>();
            try
            {
                if (context.Authorization.Status == AcmeClient.AuthorizationValid)
                {
                    _log.Information("[{identifier}] Cached authorization result: {Status}", context.Identifier, context.Authorization.Status);
                    if (!runLevel.HasFlag(RunLevel.Test) && !runLevel.HasFlag(RunLevel.IgnoreCache))
                    {
                        return;
                    }
                    // Used to make --force or --test re-validation errors non-fatal
                    _log.Information("[{identifier}] Handling challenge anyway because --test and/or --force is active");
                    context.Success = true;
                }

                _log.Information("[{identifier}] Authorizing...", context.Identifier);
                _log.Verbose("[{identifier}] Initial authorization status: {status}", context.Identifier, context.Authorization.Status);
                _log.Verbose("[{identifier}] Challenge types available: {challenges}", context.Identifier, context.Authorization.Challenges.Select(x => x.Type ?? "[Unknown]"));
                var challenge = context.Authorization.Challenges.FirstOrDefault(c => string.Equals(c.Type, context.ChallengeType, StringComparison.CurrentCultureIgnoreCase));
                if (challenge == null)
                {
                    if (context.Success == true)
                    {
                        var usedType = context.Authorization.Challenges.
                            Where(x => x.Status == AcmeClient.ChallengeValid).
                            FirstOrDefault();
                        _log.Warning("[{identifier}] Expected challenge type {type} not available, already validated using {valided}.",
                            context.Identifier,
                            context.ChallengeType,
                            usedType?.Type ?? "[unknown]");
                        return;
                    }
                    else
                    {
                        _log.Error("[{identifier}] Expected challenge type {type} not available.",
                            context.Identifier,
                            context.ChallengeType);
                        context.AddErrorMessage("Expected challenge type not available", context.Success == false);
                        return;
                    }
                }
                else
                {
                    _log.Verbose("[{identifier}] Initial challenge status: {status}", context.Identifier, challenge.Status);
                    if (challenge.Status == AcmeClient.ChallengeValid)
                    {
                        // We actually should not get here because if one of the
                        // challenges is valid, the authorization itself should also 
                        // be valid.
                        if (!runLevel.HasFlag(RunLevel.Test) && !runLevel.HasFlag(RunLevel.IgnoreCache))
                        {
                            _log.Information("[{identifier}] Cached challenge result: {Status}", context.Identifier, context.Authorization.Status);
                            return;
                        }
                    }
                }
                _log.Information("[{identifier}] Authorizing using {challengeType} validation ({name})",
                    context.Identifier,
                    context.ChallengeType,
                    context.PluginName);
                try
                {
                    // Now that we're going to call into PrepareChallenge, we will assume 
                    // responsibility to also call CleanUp later, which is signalled by
                    // the Challenge propery being not null
                    context.ChallengeDetails = await client.DecodeChallengeValidation(context.Authorization, challenge);
                    context.Challenge = challenge;
                    await context.ValidationPlugin.PrepareChallenge(context);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "[{identifier}] Error preparing for challenge answer", context.Identifier);
                    context.AddErrorMessage("Error preparing for challenge answer", context.Success == false);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Error("[{identifier}] Error preparing challenge answer", context.Identifier);
                var message = _exceptionHandler.HandleException(ex);
                context.AddErrorMessage(message, context.Success == false);
            }
        }

        /// <summary>
        /// Make sure we have authorization for every host in target
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        private async Task AnswerChallenge(ValidationContext validationContext)
        {
            if (validationContext.Challenge == null)
            {
                throw new InvalidOperationException();
            }
            try
            {
                _log.Debug("[{identifier}] Submitting challenge answer", validationContext.Identifier);
                var client = validationContext.Scope.Resolve<AcmeClient>();
                var updatedChallenge = await client.AnswerChallenge(validationContext.Challenge);
                validationContext.Challenge = updatedChallenge;
                if (updatedChallenge.Status != AcmeClient.ChallengeValid)
                {
                    if (updatedChallenge.Error != null)
                    {
                        _log.Error(updatedChallenge.Error.ToString());
                    }
                    _log.Error("[{identifier}] Authorization result: {Status}", validationContext.Identifier, updatedChallenge.Status);
                    validationContext.AddErrorMessage(updatedChallenge.Error?.ToString() ?? "Unspecified error", validationContext.Success == false);
                    return;
                }
                else
                {
                    _log.Information("[{identifier}] Authorization result: {Status}", validationContext.Identifier, updatedChallenge.Status);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Error("[{identifier}] Error submitting challenge answer", validationContext.Identifier);
                var message = _exceptionHandler.HandleException(ex);
                validationContext.AddErrorMessage(message, validationContext.Success == false);
            }
        }

        /// <summary>
        /// Clean up after (succesful or unsuccesful) validation attempt
        /// </summary>
        /// <param name="validationContext"></param>
        /// <returns></returns>
        private async Task CleanValidation(ValidationContext validationContext)
        {
            if (validationContext.Challenge == null ||
                validationContext.ValidationPlugin == null)
            {
                throw new InvalidOperationException();
            }
            try
            {
                _log.Verbose("[{identifier}] Starting post-validation cleanup", validationContext.Identifier);
                await validationContext.ValidationPlugin.CleanUp(validationContext);
                _log.Verbose("[{identifier}] Post-validation cleanup was succesful", validationContext.Identifier);
            }
            catch (Exception ex)
            {
                _log.Warning("[{identifier}] An error occured during post-validation cleanup: {ex}", ex.Message, validationContext.Identifier);
            }
        }
    }
}
