﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="ProjectManager.cs" company="Wild Gums">
//   Copyright (c) 2008 - 2015 Wild Gums. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace Orc.ProjectManagement
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using Catel;
    using Catel.Collections;
    using Catel.Data;
    using Catel.IoC;
    using Catel.Logging;
    using Catel.Reflection;

    internal class ProjectManager : IProjectManager, INeedCustomInitialization
    {
        #region Fields
        private static readonly ILog Log = LogManager.GetCurrentClassLogger();

        private readonly IProjectValidator _projectValidator;
        private readonly IProjectRefresherSelector _projectRefresherSelector;
        private readonly IProjectSerializerSelector _projectSerializerSelector;
        private readonly IProjectInitializer _projectInitializer;
        private readonly IProjectManagementInitializationService _projectManagementInitializationService;

        private bool _isLoading;
        private int _savingCounter;
        private readonly ListDictionary<string, IProject> _projects;
        private readonly IDictionary<string, IProjectRefresher> _projectRefreshers;
        #endregion

        #region Constructors
        public ProjectManager(IProjectValidator projectValidator, IProjectRefresherSelector projectRefresherSelector, IProjectSerializerSelector projectSerializerSelector,
            IProjectInitializer projectInitializer, IProjectManagementConfigurationService projectManagementConfigurationService, 
            IProjectManagementInitializationService projectManagementInitializationService)
        {
            Argument.IsNotNull(() => projectValidator);
            Argument.IsNotNull(() => projectRefresherSelector);
            Argument.IsNotNull(() => projectSerializerSelector);
            Argument.IsNotNull(() => projectInitializer);
            Argument.IsNotNull(() => projectManagementConfigurationService);
            Argument.IsNotNull(() => projectManagementInitializationService);

            _projectValidator = projectValidator;
            _projectRefresherSelector = projectRefresherSelector;
            _projectSerializerSelector = projectSerializerSelector;
            _projectInitializer = projectInitializer;
            _projectManagementInitializationService = projectManagementInitializationService;

            _projects = new ListDictionary<string, IProject>();
            _projectRefreshers = new ConcurrentDictionary<string, IProjectRefresher>();

            ProjectManagementType = projectManagementConfigurationService.GetProjectManagementType();
        }
        
        void INeedCustomInitialization.Initialize()
        {
            _projectManagementInitializationService.Initialize(this);
        }
        #endregion

        #region Properties
        public ProjectManagementType ProjectManagementType { get; private set; }

        public virtual IEnumerable<IProject> Projects
        {
            get { return _projects.Select(x => x.Value); }
        }

        public virtual IProject ActiveProject { get; set; }
        #endregion

        #region Events
        public event EventHandler<ProjectEventArgs> ProjectRefreshRequired;

        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectLoading;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectLoadingFailed;
        public event AsyncEventHandler<ProjectEventArgs> ProjectLoadingCanceled;
        public event AsyncEventHandler<ProjectEventArgs> ProjectLoaded;

        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectSaving;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectSavingFailed;
        public event AsyncEventHandler<ProjectEventArgs> ProjectSavingCanceled;
        public event AsyncEventHandler<ProjectEventArgs> ProjectSaved;

        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectRefreshing;
        public event AsyncEventHandler<ProjectEventArgs> ProjectRefreshed;
        public event AsyncEventHandler<ProjectEventArgs> ProjectRefreshingCanceled;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectRefreshingFailed;

        public event AsyncEventHandler<ProjectCancelEventArgs> ProjectClosing;
        public event AsyncEventHandler<ProjectEventArgs> ProjectClosingCanceled;
        public event AsyncEventHandler<ProjectEventArgs> ProjectClosed;

        public event AsyncEventHandler<ProjectUpdatingCancelEventArgs> ProjectActivation;
        public event AsyncEventHandler<ProjectUpdatedEventArgs> ProjectActivated;
        public event AsyncEventHandler<ProjectEventArgs> ProjectActivationCanceled;
        public event AsyncEventHandler<ProjectErrorEventArgs> ProjectActivationFailed;
        #endregion

        #region IProjectManager Members
        public async Task Initialize()
        {
            foreach (var location in _projectInitializer.GetInitialLocations().Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                Log.Debug("Loading initial project from location '{0}'", location);

                await Load(location);
            }
        }

        public async Task<bool> Refresh()
        {
            var project = ActiveProject;
            if (project == null)
            {
                return false;
            }

            return await Refresh(project);
        }

        public virtual async Task<bool> Refresh(IProject project)
        {
            Argument.IsNotNull(() => project);

            var projectLocation = project.Location;

            var activeProjectLocation = this.GetActiveProjectLocation();

            Log.Debug("Refreshing project from '{0}'", projectLocation);

            var cancelEventArgs = new ProjectCancelEventArgs(projectLocation);

            await ProjectRefreshing.SafeInvoke(this, cancelEventArgs);

            Exception error = null;
            IValidationContext validationContext = null;

            try
            {
                if (cancelEventArgs.Cancel)
                {
                    await ProjectRefreshingCanceled.SafeInvoke(this, new ProjectErrorEventArgs(project));
                    return false;
                }

                var isRefreshingActiveProject = string.Equals(activeProjectLocation, projectLocation);

                await CloseAndRemoveProject(project);

                if (isRefreshingActiveProject)
                {
                    await SetActiveProject(null);
                }

                var loadedProject = await QuietlyLoadProject(projectLocation);

                validationContext = await _projectValidator.ValidateProjectAsync(loadedProject);
                if (validationContext.HasErrors)
                {
                    Log.ErrorAndThrowException<InvalidOperationException>(string.Format("Project data was loaded from '{0}', but the validator returned errors", projectLocation));
                }

                ActivateAndAddProject(loadedProject);

                await ProjectRefreshed.SafeInvoke(this, new ProjectEventArgs(loadedProject));

                if (isRefreshingActiveProject)
                {
                    await SetActiveProject(loadedProject);
                }

                Log.Info("Refreshed project from '{0}'", projectLocation);
            }
            catch (Exception exception)
            {
                error = exception;
            }

            if (error == null)
            {
                return true;
            }

            var eventArgs = new ProjectErrorEventArgs(project,
                new ProjectException(project, string.Format("Failed to load project from location '{0}' while refreshing.", projectLocation), error),
                validationContext);
            await ProjectRefreshingFailed.SafeInvoke(this, eventArgs);

            return false;
        }

        public virtual async Task<bool> Load(string location)
        {
            Argument.IsNotNullOrWhitespace("location", location);

            var project = await LoadProject(location);

            await SetActiveProject(project);

            return project != null;
        }

        public virtual async Task<bool> LoadInactive(string location)
        {
            Argument.IsNotNullOrWhitespace("location", location);

            var project = await LoadProject(location);

            return project != null;
        }

        private async Task<IProject> LoadProject(string location)
        {
            Argument.IsNotNullOrWhitespace("location", location);

            var project = Projects.FirstOrDefault(x => string.Equals(location, x.Location, StringComparison.OrdinalIgnoreCase));
            if (project != null)
            {
                return project;
            }

            if (_projects.Count > 0 && ProjectManagementType == ProjectManagementType.SingleDocument)
            {
                Log.ErrorAndThrowException<SdiProjectManagementException>("Cannot load project '{0}', currently in SDI mode", location);
            }

            using (new DisposableToken(null, token => _isLoading = true, token => _isLoading = false))
            {
                Log.Debug("Loading project from '{0}'", location);

                var cancelEventArgs = new ProjectCancelEventArgs(location);

                await ProjectLoading.SafeInvoke(this, cancelEventArgs);

                if (cancelEventArgs.Cancel)
                {
                    Log.Debug("Canceled loading of project from '{0}'", location);
                    await ProjectLoadingCanceled.SafeInvoke(this, new ProjectEventArgs(location));

                    return null;
                }

                Exception error = null;

                IValidationContext validationContext = null;

                try
                {
                    project = await QuietlyLoadProject(location);

                    validationContext = await _projectValidator.ValidateProjectAsync(project);
                    if (validationContext.HasErrors)
                    {
                        Log.ErrorAndThrowException<InvalidOperationException>(string.Format("Project data was loaded from '{0}', but the validator returned errors", location));
                    }

                    ActivateAndAddProject(project);
                }
                catch (Exception ex)
                {
                    error = ex;
                    Log.Error(ex, "Failed to load project from '{0}'", location);
                }

                if (error != null)
                {
                    await ProjectLoadingFailed.SafeInvoke(this, new ProjectErrorEventArgs(location, error, validationContext));

                    return null;
                }
                await ProjectLoaded.SafeInvoke(this, new ProjectEventArgs(project));

                Log.Info("Loaded project from '{0}'", location);
            }

            return project;
        }

        private void ActivateAndAddProject(IProject project)
        {
            var projectLocation = project.Location;
            _projects[projectLocation] = project;

            InitializeProjectRefresher(projectLocation);
        }

        private async Task<IProject> QuietlyLoadProject(string location)
        {
            Log.Debug("Validating to see if we can load the project from '{0}'", location);

            if (!await _projectValidator.CanStartLoadingProjectAsync(location))
            {
                throw new ProjectException(location, String.Format("Cannot load project from '{0}'", location));
            }

            var projectReader = _projectSerializerSelector.GetReader(location);
            if (projectReader == null)
            {
                Log.ErrorAndThrowException<InvalidOperationException>(string.Format("No project reader is found for location '{0}'", location));
            }

            Log.Debug("Using project reader '{0}'", projectReader.GetType().Name);

            var project = await projectReader.Read(location);
            if (project == null)
            {
                Log.ErrorAndThrowException<InvalidOperationException>(string.Format("Project could not be loaded from '{0}'", location));
            }

            return project;
        }

        public async Task<bool> Save(string location = null)
        {
            var project = ActiveProject;
            if (project == null)
            {
                Log.Error("Cannot save empty project");
                return false;
            }

            if (string.IsNullOrWhiteSpace(location))
            {
                location = project.Location;
            }

            return await Save(project, location);
        }

        public async Task<bool> Save(IProject project, string location = null)
        {
            Argument.IsNotNull(() => project);

            if (string.IsNullOrWhiteSpace(location))
            {
                location = project.Location;
            }

            using (new DisposableToken(null, token => _savingCounter++, token => _savingCounter--))
            {
                Log.Debug("Saving project '{0}' to '{1}'", project, location);

                var cancelEventArgs = new ProjectCancelEventArgs(project);
                await ProjectSaving.SafeInvoke(this, cancelEventArgs);

                if (cancelEventArgs.Cancel)
                {
                    Log.Debug("Canceled saving of project to '{0}'", location);
                    await ProjectSavingCanceled.SafeInvoke(this, new ProjectEventArgs(project));
                    return false;
                }

                var projectWriter = _projectSerializerSelector.GetWriter(location);
                if (projectWriter == null)
                {
                    Log.ErrorAndThrowException<NotSupportedException>(string.Format("No project writer is found for location '{0}'", location));
                }

                Log.Debug("Using project writer '{0}'", projectWriter.GetType().Name);

                Exception error = null;
                try
                {
                    await projectWriter.Write(project, location);
                }
                catch (Exception ex)
                {
                    error = ex;
                    Log.Error(ex, "Failed to save project '{0}' to '{1}'", project, location);
                }

                if (error != null)
                {
                    await ProjectSavingFailed.SafeInvoke(this, new ProjectErrorEventArgs(project, error));

                    return false;
                }

                await ProjectSaved.SafeInvoke(this, new ProjectEventArgs(project));

                var peojectString = project.ToString();
                Log.Info("Saved project '{0}' to '{1}'", peojectString, location);
            }

            return true;
        }

        public async Task<bool> Close()
        {
            var project = ActiveProject;
            if (project == null)
            {
                return false;
            }

            return await Close(project);
        }

        public virtual async Task<bool> Close(IProject project)
        {
            Argument.IsNotNull(() => project);

            Log.Debug("Closing project '{0}'", project);

            var cancelEventArgs = new ProjectCancelEventArgs(project);
            await ProjectClosing.SafeInvoke(this, cancelEventArgs);

            if (cancelEventArgs.Cancel)
            {
                Log.Debug("Canceled closing project '{0}'", project);
                await ProjectClosingCanceled.SafeInvoke(this, new ProjectEventArgs(project));
                return false;
            }

            await SetActiveProject(null);

            await CloseAndRemoveProject(project);

            await ProjectClosed.SafeInvoke(this, new ProjectEventArgs(project));

            Log.Info("Closed project '{0}'", project);

            return true;
        }

        private async Task CloseAndRemoveProject(IProject project)
        {
            var location = project.Location;
            if (_projects.ContainsKey(location))
            {
                _projects.Remove(location);
            }

            ReleaseProjectRefresher(project);
        }

        public virtual async Task<bool> SetActiveProject(IProject project)
        {
            var activeProject = ActiveProject;

            var activeProjectLocation = activeProject == null ? null : activeProject.Location;
            var newProjectLocation = project == null ? null : project.Location;

            if (string.Equals(activeProjectLocation, newProjectLocation))
            {
                return false;
            }

            var eventArgs = new ProjectUpdatingCancelEventArgs(activeProject, project);

            await ProjectActivation.SafeInvoke(this, eventArgs);

            if (eventArgs.Cancel)
            {
                await ProjectActivationCanceled.SafeInvoke(this, new ProjectEventArgs(project));
                return false;
            }

            Exception exception = null;

            try
            {
                ActiveProject = project;
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            if (exception != null)
            {
                await ProjectActivationFailed.SafeInvoke(this, new ProjectErrorEventArgs(project, exception));
                return false;
            }


            await ProjectActivated.SafeInvoke(this, new ProjectUpdatedEventArgs(activeProject, project));

            return true;
        }

        [ObsoleteEx(ReplacementTypeOrMember = "IProjectActivationHistoryService", TreatAsErrorFromVersion = "1.0.0", RemoveInVersion = "1.1.0")]
        public IEnumerable<string> GetActivationHistory()
        {
            throw new NotSupportedException();
        }

        private void InitializeProjectRefresher(string projectLocation)
        {
            IProjectRefresher projectRefresher;

            if (!_projectRefreshers.TryGetValue(projectLocation, out projectRefresher) || projectRefresher == null)
            {
                try
                {
                    projectRefresher = _projectRefresherSelector.GetProjectRefresher(projectLocation);

                    if (projectRefresher != null)
                    {
                        Log.Debug("Subscribing to project refresher '{0}'", projectRefresher.GetType().GetSafeFullName());

                        projectRefresher.Updated += OnProjectRefresherUpdated;
                        projectRefresher.Subscribe();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to subscribe to project refresher");
                }

                if (projectRefresher != null)
                {
                    _projectRefreshers[projectLocation] = projectRefresher;
                }
            }
        }

        private void ReleaseProjectRefresher(IProject project)
        {
            IProjectRefresher projectRefresher;

            var location = project.Location;

            if (_projectRefreshers.TryGetValue(location, out projectRefresher) && projectRefresher != null)
            {
                try
                {
                    Log.Debug("Unsubscribing from project refresher '{0}'", projectRefresher.GetType().GetSafeFullName());

                    projectRefresher.Unsubscribe();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to unsubscribe from project refresher");
                }

                projectRefresher.Updated -= OnProjectRefresherUpdated;

                _projectRefreshers.Remove(location);
            }
        }

        private void OnProjectRefresherUpdated(object sender, ProjectEventArgs e)
        {
            // TODO: use dictionary for detecting if e.Project is currently loading or saving
            if (_isLoading || _savingCounter > 0)
            {
                return;
            }

            ProjectRefreshRequired.SafeInvoke(this, e);
        }
        #endregion
    }
}