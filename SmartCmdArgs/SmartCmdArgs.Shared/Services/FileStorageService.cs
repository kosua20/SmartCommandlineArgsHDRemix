﻿using SmartCmdArgs.Helper;
using SmartCmdArgs.DataSerialization;
using SmartCmdArgs.ViewModel;
using SmartCmdArgs.Wrapper;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmartCmdArgs.Services
{
    internal interface IFileStorageService
    {
        event EventHandler<FileStorageChangedEventArgs> FileStorageChanged;

        void AddProject(IVsHierarchyWrapper project);
        void RemoveAllProjects();
        void RemoveProject(IVsHierarchyWrapper project);
        void RenameProject(IVsHierarchyWrapper project, string oldProjectDir, string oldProjectName);
        void SaveSettings();
        SettingsJson ReadSettings();
        ProjectDataJson ReadDataForProject(IVsHierarchyWrapper project);
        void DeleteAllUnusedArgFiles();
        void SaveProject(IVsHierarchyWrapper project);
        void SaveAllProjects();
        IEnumerable<string> GetAllArgJsonFileNames();
    }

    enum FileStorageChanedType
    {
        Project,
        Solution,
        Settings,
    }

    class FileStorageChangedEventArgs : EventArgs
    {
        public readonly FileStorageChanedType Type;

        /// <summary>
        /// The project for that the event was triggered.
        /// Can be null if solution file triggered the event.
        /// </summary>
        public readonly IVsHierarchyWrapper Project;

        public bool IsSolutionWide => Type == FileStorageChanedType.Solution;

        public FileStorageChangedEventArgs(IVsHierarchyWrapper project)
        {
            Project = project;
            Type = project == null ? FileStorageChanedType.Solution : FileStorageChanedType.Project;
        }

        public FileStorageChangedEventArgs(FileStorageChanedType type)
        {
            Project = null;
            Type = type;
        }
    }

    internal class FileStorageService : IFileStorageService
    {
        private readonly IVisualStudioHelperService vsHelper;
        private readonly IOptionsSettingsService optionsSettings;
        private readonly IItemPathService itemPathService;
        private readonly SettingsViewModel settingsViewModel;
        private readonly TreeViewModel treeViewModel;
        private readonly Lazy<ILifeCycleService> lifeCycleService;

        private FileSystemWatcher settingsFsWatcher;
        private Dictionary<Guid, FileSystemWatcher> projectFsWatchers = new Dictionary<Guid, FileSystemWatcher>();
        private FileSystemWatcher solutionFsWatcher;

        public event EventHandler<FileStorageChangedEventArgs> FileStorageChanged;

        public FileStorageService(
            IVisualStudioHelperService vsHelper,
            IOptionsSettingsService optionsSettings,
            IItemPathService itemPathService,
            SettingsViewModel settingsViewModel,
            TreeViewModel treeViewModel,
            Lazy<ILifeCycleService> lifeCycleService)
        {
            this.vsHelper = vsHelper;
            this.optionsSettings = optionsSettings;
            this.itemPathService = itemPathService;
            this.settingsViewModel = settingsViewModel;
            this.treeViewModel = treeViewModel;
            this.lifeCycleService = lifeCycleService;
        }

        public void AddProject(IVsHierarchyWrapper project)
        {
            AttachFsWatcherToProject(project);
            AttachSolutionWatcher();
        }

        public void RemoveAllProjects()
        {
            DetachFsWatcherFromAllProjects();
            DetachSolutionWatcher();
        }

        public void RemoveProject(IVsHierarchyWrapper project)
        {
            DetachFsWatcherFromProject(project);
        }

        public void RenameProject(IVsHierarchyWrapper project, string oldProjectDir, string oldProjectName)
        {
            if (optionsSettings.UseSolutionDir)
                return;

            var guid = project.GetGuid();
            if (projectFsWatchers.TryGetValue(guid, out FileSystemWatcher fsWatcher))
            {
                projectFsWatchers.Remove(guid);
                using (fsWatcher.TemporarilyDisable())
                {
                    var newFileName = FullFilenameForProjectJsonFileFromProject(project);
                    var oldFileName = FullFilenameForProjectJsonFileFromProjectPath(oldProjectDir, oldProjectName);

                    Logger.Info($"Renaming json-file '{oldFileName}' to new name '{newFileName}'");

                    if (newFileName != oldFileName)
                    {
                        if (File.Exists(newFileName))
                        {
                            File.Delete(oldFileName);

                            FireFileStorageChanged(project);
                        }
                        else if (File.Exists(oldFileName))
                        {
                            File.Move(oldFileName, newFileName);
                        }

                        fsWatcher.Path = Path.GetDirectoryName(newFileName); ;
                        fsWatcher.Filter = Path.GetFileName(newFileName);
                    }
                }
                projectFsWatchers.Add(guid, fsWatcher);
            }
        }

        private string GetSettingsPath()
        {
            string slnFilename = vsHelper.GetSolutionFilename();

            if (slnFilename == null)
                return null;

            return Path.ChangeExtension(slnFilename, "ArgsCfg.json");
        }

        public void SaveSettings()
        {
            if (!lifeCycleService.Value.IsEnabled)
                return;

            string jsonFilename = GetSettingsPath();

            if (jsonFilename == null) return;

            using (settingsFsWatcher?.TemporarilyDisable())
            {
                if (optionsSettings.SaveSettingsToJson)
                {
                    string jsonStr = SettingsSerializer.Serialize(settingsViewModel);

                    if (jsonStr == "{}")
                        File.Delete(jsonFilename);
                    else
                        File.WriteAllText(jsonFilename, jsonStr);
                }
                else
                {
                    File.Delete(jsonFilename);
                }
            }
        }

        public SettingsJson ReadSettings()
        {
            AttachSettingsWatcher();

            string jsonFilename = GetSettingsPath();

            if (jsonFilename != null && File.Exists(jsonFilename))
            {
                string jsonStr = File.ReadAllText(jsonFilename);

                return SettingsSerializer.Deserialize(jsonStr);
            }

            return null;
        }

        public ProjectDataJson ReadDataForProject(IVsHierarchyWrapper project)
        {
            ProjectDataJson result = null;

            if (!optionsSettings.UseSolutionDir)
            {
                string filePath = FullFilenameForProjectJsonFileFromProject(project);

                if (File.Exists(filePath))
                {
                    try
                    {
                        using (Stream fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read))
                        {
                            result = DataSerialization.ProjectDataSerializer.Deserialize(fileStream);
                        }
                        Logger.Info($"Read {result?.Items?.Count} commands for project '{project.GetName()}' from json-file '{filePath}'.");
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"Failed to read file '{filePath}' with error '{e}'.");
                        result = null;
                    }
                }
                else
                {
                    Logger.Info($"Json-file '{filePath}' doesn't exists.");
                }

                return result;
            }
            else
            {
                string jsonFilename = FullFilenameForSolutionJsonFile();

                if (File.Exists(jsonFilename))
                {
                    try
                    {
                        using (Stream fileStream = File.Open(jsonFilename, FileMode.Open, FileAccess.Read))
                        {
                            SolutionDataJson slnData = SolutionDataSerializer.Deserialize(fileStream);

                            Guid projectGui = project.GetGuid();
                            result = slnData.ProjectArguments.FirstOrDefault(p => p.Id == projectGui);

                            if (result == null)
                            {
                                string projectFullName = vsHelper.GetUniqueName(project);
                                result = slnData.ProjectArguments.FirstOrDefault(p => p.Command == projectFullName);
                            }
                        }
                        Logger.Info($"Read {result?.Items?.Count} commands for project '{project.GetName()}' from json-file '{jsonFilename}'.");
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"Failed to read file '{jsonFilename}' with error '{e}'.");
                        result = null;
                    }
                }
                else
                {
                    Logger.Info($"Json-file '{jsonFilename}' doesn't exists.");
                }

                return result;
            }
        }

        public void DeleteAllUnusedArgFiles()
        {
            if (!lifeCycleService.Value.IsEnabled)
                return;

            if (!optionsSettings.DeleteUnnecessaryFilesAutomatically)
                return;

            IEnumerable<string> fileNames;
            if (optionsSettings.UseSolutionDir)
                fileNames = vsHelper.GetSupportedProjects().Select(FullFilenameForProjectJsonFileFromProject);
            else
                fileNames = new[] { FullFilenameForSolutionJsonFile() };

            foreach (var fileName in fileNames)
            {
                try
                {
                    File.Delete(fileName);
                }
                catch (Exception e)
                {
                    Logger.Warn($"Couldn't delete '{fileName}': {e}");
                }
            }
        }

        public void SaveProject(IVsHierarchyWrapper project)
        {
            if (optionsSettings.UseSolutionDir)
                SaveJsonForSolution();
            else
                SaveJsonForProject(project);
        }

        public void SaveAllProjects()
        {
            if (optionsSettings.UseSolutionDir)
                SaveJsonForSolution();
            else
                vsHelper.GetSupportedProjects().ForEach(SaveJsonForProject);
        }

        public IEnumerable<string> GetAllArgJsonFileNames()
        {
            if (!optionsSettings.VcsSupportEnabled)
            {
                return Enumerable.Empty<string>();
            }

            IEnumerable<string> result;
            if (optionsSettings.UseSolutionDir)
            {
                result = new[] { FullFilenameForSolutionJsonFile() };
            }
            else
            {
                result = vsHelper.GetSupportedProjects().Select(p => FullFilenameForProjectJsonFileFromProject(p));
            }

            return result;
        }

        private void SaveJsonForSolution()
        {
            if (!lifeCycleService.Value.IsEnabled || !optionsSettings.VcsSupportEnabled)
                return;

            string jsonFilename = FullFilenameForSolutionJsonFile();

            using (solutionFsWatcher?.TemporarilyDisable())
            {
                var allItemsExceptEmptyProjects = treeViewModel.AllItems.Where(i => !(i is CmdProject) || ((i as CmdProject).NeedsSaving()));
                if ( allItemsExceptEmptyProjects.Any() || !optionsSettings.DeleteEmptyFilesAutomatically)
                {
                    if (!vsHelper.CanEditFile(jsonFilename))
                    {
                        Logger.Error($"VS or the user did no let us edit our file :/ '{jsonFilename}'");
                    }
                    else
                    {
                        try
                        {
                            using (Stream fileStream = File.Open(jsonFilename, FileMode.Create, FileAccess.Write))
                            {
                                var solutionData = SolutionDataSerializer.Serialize(treeViewModel);

                                foreach(var projectData in solutionData.ProjectArguments)
                                {
                                    var project = vsHelper.HierarchyForProjectGuid(projectData.Id);
                                    projectData.Command = vsHelper.GetUniqueName(project);
                                }

                                SolutionDataSerializer.Serialize(solutionData, fileStream);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Warn($"Failed to write to file '{jsonFilename}' with error '{e}'.");
                        }
                    }
                }
                else
                {
                    Logger.Info("Deleting solution json file because no project has command arguments but json file exists.");

                    try
                    {
                        File.Delete(jsonFilename);
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"Failed to delete file '{jsonFilename}' with error '{e}'.");
                    }
                }
            }
        }

        private void SaveJsonForProject(IVsHierarchyWrapper project)
        {
            if (!lifeCycleService.Value.IsEnabled || !optionsSettings.VcsSupportEnabled || project == null)
                return;

            var guid = project.GetGuid();
            var vm = treeViewModel.Projects.GetValueOrDefault(guid);
            string filePath = FullFilenameForProjectJsonFileFromProject(project);
            FileSystemWatcher fsWatcher = projectFsWatchers.GetValueOrDefault(guid);

            if (vm != null && (vm.NeedsSaving() || !optionsSettings.DeleteEmptyFilesAutomatically))
            {
                using (fsWatcher?.TemporarilyDisable())
                {
                    // Tell VS that we're about to change this file
                    // This matters if the user has TFVC with server workpace (see #57)

                    if (!vsHelper.CanEditFile(filePath))
                    {
                        Logger.Error($"VS or the user did no let us edit our file :/");
                    }
                    else
                    {
                        try
                        {
                            using (Stream fileStream = File.Open(filePath, FileMode.Create, FileAccess.Write))
                            {
                                ProjectDataSerializer.Serialize(vm, fileStream);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Warn($"Failed to write to file '{filePath}' with error '{e}'.");
                        }
                    }
                }
            }
            else if (File.Exists(filePath) && optionsSettings.DeleteEmptyFilesAutomatically)
            {
                Logger.Info("Deleting json file because command list is empty but json-file exists.");

                try
                {
                    File.Delete(filePath);
                }
                catch (Exception e)
                {
                    Logger.Warn($"Failed to delete file '{filePath}' with error '{e}'.");
                }
            }
        }

        private string FullFilenameForProjectJsonFileFromProject(IVsHierarchyWrapper project)
        {
            var userFilename = vsHelper.GetMSBuildPropertyValue(project, "SmartCmdArgJsonFile");

            if (!string.IsNullOrEmpty(userFilename))
            {
                // It's recommended to use absolute paths for the json file in the first place...
                userFilename = Path.GetFullPath(userFilename); // ... but make it absolute in any case.

                Logger.Info($"'SmartCmdArgJsonFile' msbuild property present in project '{project.GetName()}' will use json file '{userFilename}'.");
                return userFilename;
            }
            else
            {
                return FullFilenameForProjectJsonFileFromProjectPath(project.GetProjectDir(), project.GetName());
            }
        }

        private string FullFilenameForSolutionJsonFile()
        {
            string slnFilename = vsHelper.GetSolutionFilename();
            string jsonFileName = Path.ChangeExtension(slnFilename, "args.json");

            if (optionsSettings.UseCustomJsonRoot)
            {
                var absoluteCustomJsonPath = itemPathService.MakePathAbsoluteBasedOnSolutionDir(optionsSettings.JsonRootPath);
                if (!string.IsNullOrWhiteSpace(absoluteCustomJsonPath))
                    jsonFileName = Path.Combine(absoluteCustomJsonPath, Path.GetFileName(jsonFileName));
            }

            return jsonFileName;
        }

        private string FullFilenameForProjectJsonFileFromProjectPath(string jsonDir, string projectName)
        {
            string filename = $"{projectName}.args.json";
            return Path.Combine(jsonDir, filename);
        }

        private void FireFileStorageChanged(IVsHierarchyWrapper project)
        {
            FileStorageChanged?.Invoke(this, new FileStorageChangedEventArgs(project));
        }

        private void FireFileStorageChanged(FileStorageChanedType type)
        {
            FileStorageChanged?.Invoke(this, new FileStorageChangedEventArgs(type));
        }

        private void AttachFsWatcherToProject(IVsHierarchyWrapper project)
        {
            string unrealFilename = FullFilenameForProjectJsonFileFromProject(project);
            string realProjectJsonFileFullName = SymbolicLinkUtils.GetRealPath(unrealFilename);
            try
            {
                var projectJsonFileWatcher = new FileSystemWatcher();

                projectJsonFileWatcher.Path = Path.GetDirectoryName(realProjectJsonFileFullName);
                projectJsonFileWatcher.Filter = Path.GetFileName(realProjectJsonFileFullName);

                projectJsonFileWatcher.EnableRaisingEvents = true;
                projectFsWatchers.Add(project.GetGuid(), projectJsonFileWatcher);

                projectJsonFileWatcher.Changed += (fsWatcher, args) => {
                    Logger.Info($"SystemFileWatcher file Change '{args.FullPath}'");
                    FireFileStorageChanged(project);
                };
                projectJsonFileWatcher.Created += (fsWatcher, args) => {
                    Logger.Info($"SystemFileWatcher file Created '{args.FullPath}'");
                    FireFileStorageChanged(project);
                };
                projectJsonFileWatcher.Renamed += (fsWatcher, args) =>
                {
                    Logger.Info($"FileWachter file Renamed '{args.FullPath}'. realProjectJsonFileFullName='{realProjectJsonFileFullName}'");
                    if (realProjectJsonFileFullName == args.FullPath)
                        FireFileStorageChanged(project);
                };

                Logger.Info($"Attached FileSystemWatcher to file '{realProjectJsonFileFullName}' for project '{project.GetName()}'.");
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to attach FileSystemWatcher to file '{realProjectJsonFileFullName}' for project '{project.GetName()}' with error '{e}'.");
            }
        }

        private void DetachFsWatcherFromProject(IVsHierarchyWrapper project)
        {
            var guid = project.GetGuid();
            if (projectFsWatchers.TryGetValue(guid, out FileSystemWatcher fsWatcher))
            {
                fsWatcher.Dispose();
                projectFsWatchers.Remove(guid);
                Logger.Info($"Detached FileSystemWatcher for project '{project.GetName()}'.");
            }
        }

        private void DetachFsWatcherFromAllProjects()
        {
            foreach (var projectFsWatcher in projectFsWatchers)
            {
                projectFsWatcher.Value.Dispose();
                Logger.Info($"Detached FileSystemWatcher for project '{projectFsWatcher.Key}'.");
            }
            projectFsWatchers.Clear();
        }


        private void AttachSolutionWatcher()
        {
            if (solutionFsWatcher == null)
            {
                string slnFilename = vsHelper.GetSolutionFilename();

                if (slnFilename == null)
                    return;

                string jsonFilename = Path.ChangeExtension(slnFilename, "args.json");

                try
                {
                    solutionFsWatcher = new FileSystemWatcher();
                    solutionFsWatcher.Path = Path.GetDirectoryName(jsonFilename);
                    solutionFsWatcher.Filter = Path.GetFileName(jsonFilename);

                    solutionFsWatcher.EnableRaisingEvents = true;

                    solutionFsWatcher.Changed += (fsWatcher, args) => {
                        Logger.Info($"SystemFileWatcher file Change '{args.FullPath}'");
                        FireFileStorageChanged(null);
                    };
                    solutionFsWatcher.Created += (fsWatcher, args) => {
                        Logger.Info($"SystemFileWatcher file Created '{args.FullPath}'");
                        FireFileStorageChanged(null);
                    };
                    solutionFsWatcher.Renamed += (fsWatcher, args) =>
                    {
                        Logger.Info($"FileWachter file Renamed '{args.FullPath}'. filename='{jsonFilename}'");
                        if (jsonFilename == args.FullPath)
                            FireFileStorageChanged(null);
                    };

                    Logger.Info($"Attached FileSystemWatcher to file '{jsonFilename}' for solution.");
                }
                catch (Exception e)
                {
                    Logger.Warn($"Failed to attach FileSystemWatcher to file '{jsonFilename}' for solution with error '{e}'.");
                }
            }
        }
        private void DetachSolutionWatcher()
        {
            if (solutionFsWatcher != null)
            {
                solutionFsWatcher.Dispose();
                solutionFsWatcher = null;
            }
        }

        private void AttachSettingsWatcher()
        {
            if (settingsFsWatcher != null)
                return;

            var jsonFilename = GetSettingsPath();

            if (jsonFilename == null)
                return;

            try
            {
                settingsFsWatcher = new FileSystemWatcher();
                settingsFsWatcher.Path = Path.GetDirectoryName(jsonFilename);
                settingsFsWatcher.Filter = Path.GetFileName(jsonFilename);

                settingsFsWatcher.EnableRaisingEvents = true;

                settingsFsWatcher.Changed += (fsWatcher, args) => {
                    Logger.Info($"SystemFileWatcher file Change '{args.FullPath}'");
                    FireFileStorageChanged(FileStorageChanedType.Settings);
                };
                settingsFsWatcher.Created += (fsWatcher, args) => {
                    Logger.Info($"SystemFileWatcher file Created '{args.FullPath}'");
                    FireFileStorageChanged(FileStorageChanedType.Settings);
                };
                settingsFsWatcher.Renamed += (fsWatcher, args) =>
                {
                    Logger.Info($"FileWachter file Renamed '{args.FullPath}'. filename='{jsonFilename}'");
                    if (jsonFilename == args.FullPath)
                        FireFileStorageChanged(FileStorageChanedType.Settings);
                };

                Logger.Info($"Attached FileSystemWatcher to file '{jsonFilename}' for settings.");
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to attach FileSystemWatcher to file '{jsonFilename}' for settings with error '{e}'.");
            }
        }
    }
}
