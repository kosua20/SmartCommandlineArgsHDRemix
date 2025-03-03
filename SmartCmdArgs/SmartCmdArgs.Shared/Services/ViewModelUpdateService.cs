﻿using SmartCmdArgs.Helper;
using SmartCmdArgs.DataSerialization;
using SmartCmdArgs.ViewModel;
using SmartCmdArgs.Wrapper;
using System;
using System.Collections.Generic;
using System.Linq;
using SmartCmdArgs.View;
using Microsoft.Extensions.DependencyInjection;

namespace SmartCmdArgs.Services
{
    internal interface IViewModelUpdateService
    {
        void UpdateCommandsForProject(IVsHierarchyWrapper project, bool gatherArgsWhenNotFound = true, bool gatherArgsForEmptyViewModels = false);
        void UpdateCommandsForProjects(IEnumerable<IVsHierarchyWrapper> projects, Action<IVsHierarchyWrapper> actionAfterUpdate = null);
        void UpdateIsActiveForParamsDebounced();
        void UpdateCurrentStartupProject();
    }

    internal class ViewModelUpdateService : IViewModelUpdateService, IDisposable
    {
        private readonly ToolWindowViewModel toolWindowViewModel;
        private readonly TreeViewModel treeViewModel;
        private readonly IOptionsSettingsService optionsSettings;
        private readonly IFileStorageService fileStorage;
        private readonly IProjectConfigService projectConfig;
        private readonly ISuoDataService suoDataService;
        private readonly IItemAggregationService itemAggregation;
        private readonly IItemEvaluationService itemEvaluation;
        private readonly IVisualStudioHelperService vsHelper;
        private readonly Lazy<ILifeCycleService> lifeCycleService;

        private readonly Debouncer _updateIsActiveDebouncer;

        public ViewModelUpdateService(
            ToolWindowViewModel toolWindowViewModel,
            TreeViewModel treeViewModel,
            IOptionsSettingsService optionsSettings,
            IFileStorageService fileStorage,
            IProjectConfigService projectConfig,
            ISuoDataService suoDataService,
            IItemAggregationService itemAggregation,
            IItemEvaluationService itemEvaluation,
            IVisualStudioHelperService vsHelper,
            Lazy<ILifeCycleService> lifeCycleService)
        {
            this.toolWindowViewModel = toolWindowViewModel;
            this.treeViewModel = treeViewModel;
            this.optionsSettings = optionsSettings;
            this.fileStorage = fileStorage;
            this.projectConfig = projectConfig;
            this.suoDataService = suoDataService;
            this.itemAggregation = itemAggregation;
            this.itemEvaluation = itemEvaluation;
            this.vsHelper = vsHelper;
            this.lifeCycleService = lifeCycleService;
            _updateIsActiveDebouncer = new Debouncer(TimeSpan.FromMilliseconds(250), UpdateIsActiveForArguments);
        }

        public void Dispose()
        {
            _updateIsActiveDebouncer.Dispose();
        }

        public void UpdateCommandsForProject(IVsHierarchyWrapper project, bool gatherArgsWhenNotFound, bool gatherArgsForEmptyViewModels)
        {
            if (!lifeCycleService.Value.IsEnabled)
                return;

            if (project == null)
                throw new ArgumentNullException(nameof(project));

            Logger.Info($"Update commands for project '{project?.GetName()}'. IsVcsSupportEnabled={optionsSettings.VcsSupportEnabled}. SolutionData.Count={suoDataService.SuoDataJson?.ProjectArguments?.Count}.");

            var projectGuid = project.GetGuid();
            if (projectGuid == Guid.Empty)
            {
                Logger.Info("Skipping project because guid euqals empty.");
                return;
            }

            var solutionData = suoDataService.SuoDataJson ?? new SuoDataJson();
            var solutionDataParameters = suoDataService.ParametersOfSuoData;

            // joins data from solution and project
            //  => overrides solution commands for a project if a project json file exists
            //  => keeps all data from the suo file for projects without a json
            //  => if we have data in our ViewModel we use this instad of the suo file

            // get project json data
            ProjectDataJson projectData = null;
            if (optionsSettings.VcsSupportEnabled)
            {
                projectData = fileStorage.ReadDataForProject(project);
            }

            var saveData = false;

            // data in project json overrides current data if it exists to respond to changes made by git to the file
            if (projectData != null)
            {
                Logger.Info($"Setting {projectData?.Items?.Count} commands for project '{project.GetName()}' from json-file.");

                var projectListViewModel = treeViewModel.Projects.GetValueOrDefault(projectGuid);

                // update enabled state of the project json data (source prio: ViewModel > suo file)
                if (projectData.Items != null)
                {
                    var argumentDataFromProject = projectData.AllParameters;
                    var argumentDataFromLVM = projectListViewModel?.AllParameters.ToDictionary(arg => arg.Id, arg => arg);
                    foreach (var dataFromProject in argumentDataFromProject)
                    {
                        if (argumentDataFromLVM != null && argumentDataFromLVM.TryGetValue(dataFromProject.Id, out CmdParameter paramFromVM))
                            dataFromProject.Enabled = paramFromVM.IsChecked;
                        else if (solutionDataParameters.Contains(dataFromProject.Id))
                            dataFromProject.Enabled = solutionData.CheckedArguments.Contains(dataFromProject.Id);
                        else
                            dataFromProject.Enabled = dataFromProject.DefaultChecked;
                    }

                    var containerDataFromProject = projectData.AllContainer;
                    var containerDataFromLVM = projectListViewModel?.AllContainer.ToDictionary(con => con.Id, con => con);
                    foreach (var dataFromProject in containerDataFromProject)
                    {
                        if (containerDataFromLVM != null && containerDataFromLVM.TryGetValue(dataFromProject.Id, out CmdContainer conFromVM))
                            dataFromProject.Expanded = conFromVM.IsExpanded;
                        else
                            dataFromProject.Expanded = solutionData.ExpandedContainer.Contains(dataFromProject.Id);
                    }

                    var itemDataFromProject = projectData.AllItems;
                    var itemDataFromLVM = projectListViewModel?.ToDictionary(item => item.Id, item => item);
                    foreach (var dataFromProject in itemDataFromProject)
                    {
                        if (itemDataFromLVM != null && itemDataFromLVM.TryGetValue(dataFromProject.Id, out CmdBase itemFromVM))
                            dataFromProject.Selected = itemFromVM.IsSelected;
                        else
                            dataFromProject.Selected = solutionData.SelectedItems.Contains(dataFromProject.Id);
                    }

                    if (projectListViewModel != null)
                    {
                        projectData.Expanded = projectListViewModel.IsExpanded;
                        projectData.Selected = projectListViewModel.IsSelected;
                    }
                    else
                    {
                        projectData.Expanded = solutionData.ExpandedContainer.Contains(projectData.Id);
                        projectData.Selected = solutionData.SelectedItems.Contains(projectData.Id);
                    }
                }
                else
                {
                    projectData = new ProjectDataJson();
                    Logger.Info($"DataCollection for project '{project.GetName()}' is null.");
                }
            }
            // if we have data in the ViewModel we keep it
            else if (treeViewModel.Projects.TryGetValue(projectGuid, out CmdProject cmdProject) && (cmdProject.Items.Count > 0 || !gatherArgsForEmptyViewModels))
            {
                return;
            }
            // if we dont have VCS enabld we try to read the suo file data
            else if (!optionsSettings.VcsSupportEnabled && solutionData.ProjectArguments.TryGetValue(projectGuid, out projectData))
            {
                Logger.Info($"Will use commands from suo file for project '{project.GetName()}'.");
                var argumentDataFromProject = projectData.AllParameters;
                foreach (var arg in argumentDataFromProject)
                {
                    arg.Enabled = solutionData.CheckedArguments.Contains(arg.Id);
                }

                var containerDataFromProject = projectData.AllContainer;
                foreach (var con in containerDataFromProject)
                {
                    con.Expanded = solutionData.ExpandedContainer.Contains(con.Id);
                }

                var itemDataFromProject = projectData.AllItems;
                foreach (var item in itemDataFromProject)
                {
                    item.Selected = solutionData.SelectedItems.Contains(item.Id);
                }

                projectData.Expanded = solutionData.ExpandedContainer.Contains(projectData.Id);
                projectData.Selected = solutionData.SelectedItems.Contains(projectData.Id);
            }
            // if we don't have suo or json data we read cmd args from the project configs
            else
            {
                projectData = new ProjectDataJson();

                if (gatherArgsWhenNotFound)
                {
                    Logger.Info($"Gathering commands from configurations for project '{project.GetName()}'.");
                    try
                    {
                        projectData.Items.AddRange(projectConfig.GetItemsFromProjectConfig(project));
                        saveData = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to gather comamnds from configurations for project '{project.GetName()}'.", ex);
                    }
                }
            }

            // push projectData to the ViewModel
            toolWindowViewModel.PopulateFromProjectData(project, projectData);

            if (saveData)
            {
                fileStorage.SaveProject(project);
            }

            Logger.Info($"Updated Commands for project '{project.GetName()}'.");
        }

        public void UpdateCommandsForProjects(IEnumerable<IVsHierarchyWrapper> projects, Action<IVsHierarchyWrapper> actionAfterUpdate)
        {
            var supportedProjects = vsHelper.GetSupportedProjects();

            List<IVsHierarchyWrapper> startupProjects = null; 

            foreach (var project in supportedProjects)
            {
                if (project.GetGuid() == Guid.Empty)
                {
                    Logger.Info($"Race condition might occurred while dispatching update commands function call. Project is already unloaded.");
                }

                var gatherArgs = true;
                if (optionsSettings.CppProjectScanHandling != CppProjectScanHandling.All && project.GetKind() == ProjectKinds.CPP)
                {
                    if (optionsSettings.CppProjectScanHandling == CppProjectScanHandling.OnlyStartup)
                    {
                        if (startupProjects == null)
                            startupProjects = vsHelper.GetStartupProjects().ToList();

                        gatherArgs = startupProjects.Contains(project);
                    }
                }

                UpdateCommandsForProject(project, gatherArgs, false);
                actionAfterUpdate?.Invoke(project);
            }
        }

        private ISet<CmdParameter> GetAllActiveItemsForProject(IVsHierarchyWrapper project)
        {
            if (!optionsSettings.ManageCommandLineArgs
                && !optionsSettings.ManageEnvironmentVars
                && !optionsSettings.ManageWorkingDirectories
                && !optionsSettings.ManageLaunchApplication)
            {
                return new HashSet<CmdParameter>();
            }

            var Args = new HashSet<CmdParameter>();
            var EnvVars = new Dictionary<string, CmdParameter>();
            CmdParameter workDir = null;
            CmdParameter launchApp = null;

            foreach (var item in itemAggregation.GetAllComamndLineParamsForProject(project))
            {
                if (item.ParamType == CmdParamType.CmdArg && optionsSettings.ManageCommandLineArgs)
                {
                    Args.Add(item);
                }
                else if (item.ParamType == CmdParamType.EnvVar && optionsSettings.ManageEnvironmentVars)
                {
                    if (itemEvaluation.TryParseEnvVar(item.Value, out EnvVar envVar))
                    {
                        EnvVars[envVar.Name] = item;
                    }
                }
                else if (item.ParamType == CmdParamType.WorkDir && optionsSettings.ManageWorkingDirectories)
                {
                    workDir = item;
                }
                else if (item.ParamType == CmdParamType.LaunchApp && optionsSettings.ManageLaunchApplication)
                {
                    launchApp = item;
                }
            }

            var result = new HashSet<CmdParameter>(Args.Concat(EnvVars.Values));

            if (workDir != null)
            {
                result.Add(workDir);
            }

            if (launchApp != null)
            {
                result.Add(launchApp);
            }

            return result;
        }

        private void UpdateIsActiveForArguments()
        {
            foreach (var cmdProject in treeViewModel.AllProjects)
            {
                if (optionsSettings.DisableInactiveItems == InactiveDisableMode.InAllProjects
                    || (optionsSettings.DisableInactiveItems != InactiveDisableMode.Disabled && cmdProject.IsStartupProject))
                {
                    var project = vsHelper.HierarchyForProjectGuid(cmdProject.Id);
                    var activeItems = GetAllActiveItemsForProject(project);

                    foreach (var item in cmdProject.AllParameters)
                    {
                        item.IsActive = activeItems.Contains(item);
                    }
                }
                else
                {
                    foreach (var item in cmdProject.AllParameters)
                    {
                        item.IsActive = true;
                    }
                }
            }
        }

        public void UpdateIsActiveForParamsDebounced()
        {
            _updateIsActiveDebouncer.CallActionDebounced();
        }

        public void UpdateCurrentStartupProject()
        {
            var startupProjects = vsHelper.GetStartupProjects().ToList();

            if (optionsSettings.CppProjectScanHandling == CppProjectScanHandling.OnlyStartup)
            {
                foreach (var project in startupProjects.Where(x => x.GetKind() == ProjectKinds.CPP))
                {
                    UpdateCommandsForProject(project, true, true);
                }
            }

            var startupProjectGuids = new HashSet<Guid>(startupProjects.Select(hierarchy => hierarchy.GetGuid()));
            treeViewModel.Projects.ForEach(p => p.Value.IsStartupProject = startupProjectGuids.Contains(p.Key));
            treeViewModel.UpdateTree();
        }
    }
}
