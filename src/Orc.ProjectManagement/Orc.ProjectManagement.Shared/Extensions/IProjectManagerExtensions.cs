﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="IProjectManagerExtensions.cs" company="Wild Gums">
//   Copyright (c) 2008 - 2015 Wild Gums. All rights reserved.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------


namespace Orc.ProjectManagement
{
    using Catel;

    public static class IProjectManagerExtensions
    {
        #region Methods
        public static TProject GetActiveProject<TProject>(this IProjectManager projectManager)
            where TProject : IProject
        {
            Argument.IsNotNull(() => projectManager);

            return (TProject)projectManager.ActiveProject;
        }

        [ObsoleteEx(ReplacementTypeOrMember = "GetActiveProject", RemoveInVersion = "1.1.0", TreatAsErrorFromVersion = "1.0.0")]
        public static TProject GetProject<TProject>(this IProjectManager projectManager)
            where TProject : IProject
        {
            Argument.IsNotNull(() => projectManager);

            return (TProject)projectManager.ActiveProject;
        }
        #endregion
    }
}