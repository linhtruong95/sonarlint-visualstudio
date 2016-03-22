//-----------------------------------------------------------------------
// <copyright file="BindingWorkflow.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Microsoft.VisualStudio.CodeAnalysis.RuleSets;
using SonarLint.VisualStudio.Integration.Progress;
using SonarLint.VisualStudio.Integration.Resources;
using SonarLint.VisualStudio.Integration.Service;
using SonarLint.VisualStudio.Integration.TeamExplorer;
using SonarLint.VisualStudio.Progress.Controller;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace SonarLint.VisualStudio.Integration.Binding
{
    /// <summary>
    /// Workflow execution for the bind command
    /// </summary>
    internal class BindingWorkflow
    {
        private readonly IHost host;
        private readonly ProjectInformation project;
        private readonly IProjectSystemHelper projectSystemHelper;
        private readonly SolutionBindingOperation solutionBindingOperation;

        internal readonly Dictionary<string, RuleSetGroup> LanguageToGroupMapping = new Dictionary<string, RuleSetGroup>
        {
            {SonarQubeServiceWrapper.CSharpLanguage, RuleSetGroup.CSharp },
            {SonarQubeServiceWrapper.VBLanguage, RuleSetGroup.VB }
        };        

        public BindingWorkflow(IHost host, ProjectInformation project)
            : this(host, project, null)
        {

        }

        internal /*for testing purposes*/ BindingWorkflow(IHost host, ProjectInformation project, IProjectSystemHelper projectSystemHelper)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            this.host = host;
            this.project = project;
            this.projectSystemHelper = projectSystemHelper ?? new ProjectSystemHelper(this.host);

            this.solutionBindingOperation = new SolutionBindingOperation(
                    this.host,
                    this.projectSystemHelper,
                    this.host.SonarQubeService.CurrentConnection,
                    this.project.Key);
        }

        #region Workflow state

        public Dictionary<RuleSetGroup, RuleSet> Rulesets
        {
            get;
        } = new Dictionary<RuleSetGroup, RuleSet>();

        public List<NuGetPackageInfo> NuGetPackages
        {
            get;
        } = new List<NuGetPackageInfo>();

        public Dictionary<RuleSetGroup, string> SolutionRulesetPaths
        {
            get;
        } = new Dictionary<RuleSetGroup, string>();

        internal /*for testing purposes*/ bool AllNuGetPackagesInstalled
        {
            get;
            set;
        } = true;

        #endregion

        #region Workflow startup

        public IProgressEvents Run()
        {
            this.host.ActiveSection?.UserNotifications?.HideNotification(NotificationIds.FailedToBindId);

            List<string> languages = new List<string>();
            if (this.projectSystemHelper.GetSolutionManagedProjects().Any(p => ProjectSystemHelper.IsCSharpProject(p)))
            {
                languages.Add(SonarQubeServiceWrapper.CSharpLanguage);
            }

            if (this.projectSystemHelper.GetSolutionManagedProjects().Any(p => ProjectSystemHelper.IsVBProject(p)))
            {
                languages.Add(SonarQubeServiceWrapper.VBLanguage);
            }

            Debug.Assert(languages.Count > 0, "Expecting managed projects in solution");
            Debug.Assert(this.host.ActiveSection != null, "Expect the section to be attached at least until this method returns");

            IProgressEvents progress = ProgressStepRunner.StartAsync(this.host,
                this.host.ActiveSection.ProgressHost,
                (controller) => this.CreateWorkflowSteps(controller, languages));

            this.DebugOnly_MonitorProgress(progress);

            return progress;
        }

        [Conditional("DEBUG")]
        private void DebugOnly_MonitorProgress(IProgressEvents progress)
        {
            progress.RunOnFinished(r => VsShellUtils.WriteToGeneralOutputPane(this.host, "DEBUGONLY: Binding workflow finished, Execution result: {0}", r));
        }

        private ProgressStepDefinition[] CreateWorkflowSteps(IProgressController controller, IEnumerable<string> languages)
        {
            StepAttributes IndeterminateNonCancellableUIStep = StepAttributes.Indeterminate | StepAttributes.NonCancellable;
            StepAttributes HiddenIndeterminateNonImpactingNonCancellableUIStep = IndeterminateNonCancellableUIStep | StepAttributes.Hidden | StepAttributes.NoProgressImpact;
            StepAttributes HiddenNonImpactingBackgroundStep = StepAttributes.BackgroundThread | StepAttributes.Hidden | StepAttributes.NoProgressImpact;

            return new ProgressStepDefinition[]
            {
                new ProgressStepDefinition(null, HiddenNonImpactingBackgroundStep,
                        (token, notifications) => notifications.ProgressChanged(Strings.StartedSolutionBindingWorkflow, double.NaN)),

                new ProgressStepDefinition(null, StepAttributes.Indeterminate | StepAttributes.Hidden,
                        (token, notifications) => this.PromptSaveSolutionIfDirty(controller, token)),

                new ProgressStepDefinition(Strings.BindingProjectsDisplayMessage, StepAttributes.BackgroundThread,
                        (token, notifications) => this.DownloadQualityProfile(controller, token, notifications, languages)),

                new ProgressStepDefinition(null, HiddenIndeterminateNonImpactingNonCancellableUIStep,
                        (token, notifications) => { NuGetHelper.LoadService(this.host); /*The service needs to be loaded on UI thread*/ }),

                new ProgressStepDefinition(Strings.BindingProjectsDisplayMessage, StepAttributes.BackgroundThread,
                        (token, notifications) => this.InstallPackages(controller, token, notifications)),

                new ProgressStepDefinition(Strings.BindingProjectsDisplayMessage, IndeterminateNonCancellableUIStep,
                        (token, notifications) => this.InitializeSolutionBindingOnUIThread(notifications)),

                new ProgressStepDefinition(Strings.BindingProjectsDisplayMessage, StepAttributes.BackgroundThread | StepAttributes.Indeterminate,
                        (token, notifications) => this.PrepareSolutionBinding(token)),

                new ProgressStepDefinition(null, StepAttributes.Hidden | StepAttributes.Indeterminate,
                        (token, notifications) => this.FinishSolutionBindingOnUIThread(controller, token)),

                new ProgressStepDefinition(null, HiddenIndeterminateNonImpactingNonCancellableUIStep,
                        (token, notifications) => this.SilentSaveSolutionIfDirty()),

                new ProgressStepDefinition(null, HiddenNonImpactingBackgroundStep,
                        (token, notifications) => this.EmitBindingCompleteMessage(notifications))
            };
        }
        #endregion

        #region Workflow steps
        internal /*for testing purposes*/ void PromptSaveSolutionIfDirty(IProgressController controller, CancellationToken token)
        {
            if (!VsShellUtils.SaveSolution(this.host, silent: false))
            {
                VsShellUtils.WriteToGeneralOutputPane(this.host, Strings.SolutionSaveCancelledBindAborted);

                this.AbortWorkflow(controller, token);
            }
        }

        internal /*for testing purposes*/ void SilentSaveSolutionIfDirty()
        {
            bool saved = VsShellUtils.SaveSolution(this.host, silent: true);
            Debug.Assert(saved, "Should not be cancellable");
        }

        internal /*for testing purposes*/ void DownloadQualityProfile(IProgressController controller, CancellationToken cancellationToken, IProgressStepExecutionEvents notificationEvents, IEnumerable<string> languages)
        {
            Debug.Assert(controller != null);
            Debug.Assert(notificationEvents != null);

            bool failed = false;
            Dictionary<string, RuleSet> rulesets = new Dictionary<string, RuleSet>();
            DeterminateStepProgressNotifier notifier = new DeterminateStepProgressNotifier(notificationEvents, languages.Count());

            foreach (var language in languages)
            {
                notifier.NotifyCurrentProgress(string.Format(CultureInfo.CurrentCulture, Strings.DownloadingQualityProfileProgressMessage, language));

                var export = this.host.SonarQubeService.GetExportProfile(this.project, language, cancellationToken);

                if (export == null)
                {
                    failed = true;
                    break;
                }

                this.NuGetPackages.AddRange(export.Deployment.NuGetPackages);

                var tempRuleSetFilePath = Path.GetTempFileName();
                File.WriteAllText(tempRuleSetFilePath, export.Configuration.RuleSet.OuterXml);
                RuleSet ruleSet = RuleSet.LoadFromFile(tempRuleSetFilePath);

                rulesets[language] = ruleSet;
                notifier.NotifyIncrementedProgress(string.Empty);
                if (rulesets[language] == null)
                {
                    failed = true;
                    break;
                }
            }

            if (failed)
            {
                VsShellUtils.WriteToGeneralOutputPane(this.host, Strings.QualityProfileDownloadFailedMessage);
                this.AbortWorkflow(controller, cancellationToken);
            }
            else
            {
                // Set the rule set which should be available for the following steps
                foreach (var keyValue in rulesets)
                {
                    this.Rulesets[this.LanguageToGroup(keyValue.Key)] = keyValue.Value;
                }

                notifier.NotifyCurrentProgress(Strings.QualityProfileDownloadedSuccessfulMessage);
            }
        }

        private void InitializeSolutionBindingOnUIThread(IProgressStepExecutionEvents notificationEvents)
        {
            Debug.Assert(System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? false, "Expected to run on UI thread");

            notificationEvents.ProgressChanged(Strings.RuleSetGenerationProgressMessage, double.NaN);

            this.solutionBindingOperation.RegisterKnownRuleSets(this.Rulesets);
            this.solutionBindingOperation.Initialize();
        }

        private void PrepareSolutionBinding(CancellationToken token)
        {
            this.solutionBindingOperation.Prepare(token);
        }

        private void FinishSolutionBindingOnUIThread(IProgressController controller, CancellationToken token)
        {
            Debug.Assert(System.Windows.Application.Current?.Dispatcher.CheckAccess() ?? false, "Expected to run on UI thread");

            if (!this.solutionBindingOperation.CommitSolutionBinding())
            {
                AbortWorkflow(controller, token);
                return;
        }
        }

        /// <summary>
        /// Will install the NuGet packages for the current managed projects.
        /// The packages that will be installed will be based on the information from <see cref="Analyzer.GetRequiredNuGetPackages"/> 
        /// and is specific to the <see cref="RuleSet"/>.
        /// </summary>
        internal /*for testing purposes*/ void InstallPackages(IProgressController controller, CancellationToken token, IProgressStepExecutionEvents notificationEvents)
        {
            if (!this.NuGetPackages.Any())
            {
                return;
            }

            Debug.Assert(this.NuGetPackages.Count == this.NuGetPackages.Distinct().Count(), "Duplicate NuGet packages specified");

            var managedProjects = this.projectSystemHelper.GetSolutionManagedProjects().ToArray();
            if (!managedProjects.Any())
            {
                Debug.Fail("Not expected to be called when there are no managed projects");
                return;
            }

            DeterminateStepProgressNotifier progressNotifier = new DeterminateStepProgressNotifier(notificationEvents, managedProjects.Length * this.NuGetPackages.Count);
            foreach (var project in managedProjects)
            {
                foreach (var packageInfo in this.NuGetPackages)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    string message = string.Format(CultureInfo.CurrentCulture, Strings.EnsuringNugetPackagesProgressMessage, packageInfo.Id, project.Name);
                    progressNotifier.NotifyCurrentProgress(message);

                    // TODO: SVS-33 (https://jira.sonarsource.com/browse/SVS-33) Trigger a Team Explorer warning notification to investigate the partial binding in the output window.
                    this.AllNuGetPackagesInstalled &= NuGetHelper.TryInstallPackage(this.host, project, packageInfo.Id, packageInfo.Version);

                    progressNotifier.NotifyIncrementedProgress(string.Empty);
                }
            }
        }

        internal /*for testing purposes*/ void EmitBindingCompleteMessage(IProgressStepExecutionEvents notifications)
            {
            var message = this.AllNuGetPackagesInstalled
                ? Strings.FinishedSolutionBindingWorkflowSuccessful
                : Strings.FinishedSolutionBindingWorkflowNotAllPackagesInstalled;
            notifications.ProgressChanged(message, double.NaN);
        }
        #endregion

        #region Helpers
        private RuleSetGroup LanguageToGroup(string language)
        {
            RuleSetGroup group;
            if (!this.LanguageToGroupMapping.TryGetValue(language, out group))
            {
                Debug.Fail("Unsupported language: " + language);
                throw new InvalidOperationException();
            }
            return group;
        }

        private void AbortWorkflow(IProgressController controller, CancellationToken token)
        {
            bool aborted = controller.TryAbort();
            Debug.Assert(aborted || token.IsCancellationRequested, "Failed to abort the workflow");
        }
        #endregion

    }
}