﻿using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.Shell.Interop;
using System.Runtime.InteropServices;
using Ankh.Ids;
using Microsoft.VisualStudio;
using Ankh.Selection;
using System.Diagnostics;
using SharpSvn;
using System.IO;

namespace Ankh.Scc.ProjectMap
{
    /// <summary>
    /// Enum of project types with workarounds
    /// </summary>
    enum SccProjectType
    {
        Normal,
        SolutionFolder,
        WebSite,
    }

    [DebuggerDisplay("Project={ProjectName}, ProjectType={_projectType}")]
    sealed partial class SccProjectData
    {
        readonly IAnkhServiceProvider _context;
        readonly IVsSccProject2 _sccProject;
        readonly IVsHierarchy _hierarchy;
        readonly IVsProject _vsProject;
        readonly SccProjectType _projectType;
        readonly SccProjectFileCollection _files;
        bool _isManaged;
        bool _isRegistered;
        bool _loaded;
        string _projectFile;
        bool _checkedProjectFile;
        AnkhSccProvider _scc;
        SvnProject _svnProjectInstance;
        string _projectName;
        string _projectDirectory;
        Guid? _projectGuid;
        Guid? _projectTypeGuid;

        public SccProjectData(IAnkhServiceProvider context, IVsSccProject2 project)
        {
            if (context == null)
                throw new ArgumentNullException("context");
            else if (project == null)
                throw new ArgumentNullException("project");

            _context = context;

            // Project references to speed up marshalling
            _sccProject = project;
            _hierarchy = (IVsHierarchy)project; // A project must be a hierarchy in VS
            _vsProject = (IVsProject)project; // A project must be a VS project
            
            _projectType = GetProjectType(project);
            _files = new SccProjectFileCollection();
        }

        public IVsSccProject2 SccProject
        {
            get { return _sccProject; }
        }

        public IVsProject VsProject
        {
            get { return _vsProject; }
        }

        public IVsHierarchy ProjectHierarchy
        {
            get { return _hierarchy; }
        }

        public bool IsManaged
        {
            get { return _isManaged; }

            // Called by IVsSccManager.RegisterSccProject() when we were preregistered
            internal set { _isManaged = value; }
        }

        public string ProjectName
        {
            get
            {
                if (_projectName == null && _hierarchy != null)
                {
                    _projectName = "";
                    object name;

                    if(ErrorHandler.Succeeded(_hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_Name, out name)))
                    {
                        _projectName = name as string;
                    }
                }

                return _projectName;
            }
        }

        /// <summary>
        /// Gets the guid of the project within the solution
        /// </summary>
        public Guid ProjectGuid
        {
            get
            {
                if(!_projectGuid.HasValue)
                {
                    IVsSolution solution = _context.GetService<IVsSolution>(typeof(SVsSolution));

                    Guid value;
                    if (ErrorHandler.Succeeded(solution.GetGuidOfProject(ProjectHierarchy, out value)))
                        _projectGuid = value;                    
                }

                return _projectGuid.HasValue ? _projectGuid.Value : Guid.Empty;
            }
        }

        /// <summary>
        /// Gets the guid of the project within the solution
        /// </summary>
        public Guid ProjectTypeGuid
        {
            get
            {
                if (!_projectTypeGuid.HasValue)
                {
                    IVsSolution solution = _context.GetService<IVsSolution>(typeof(SVsSolution));

                    Guid value;
                    if (ErrorHandler.Succeeded(solution.GetProjectTypeGuid(0, ProjectFile, out value)))
                        _projectTypeGuid = value;            
                }

                return _projectTypeGuid.HasValue ? _projectTypeGuid.Value : Guid.Empty;
            }
        }

        /// <summary>
        /// Gets the project directory.
        /// </summary>
        /// <value>The project directory or null if the project does not have one</value>
        public string ProjectDirectory
        {
            get
            {
                if (_projectDirectory == null && _hierarchy != null)
                {
                    _projectDirectory = "";
                    object name;

                    if (ErrorHandler.Succeeded(_hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ProjectDir, out name)))
                    {
                        string dir = name as string;

                        if (!string.IsNullOrEmpty(dir) && SvnItem.IsValidPath(dir))
                            dir = SvnTools.GetNormalizedFullPath(dir);
                        else
                        {
                            // Ok, we have users reporting they get here via Analysis services

                            // Wild guess: If we have a valid project file assume its folder 
                            //              is the project directory.
                            dir = ProjectFile;

                            if (dir != null)
                                dir = Path.GetDirectoryName(dir);
                            else
                                dir = ""; // Cache as invalid
                        }

                        _projectDirectory = dir;
                    }
                }

                return String.IsNullOrEmpty(_projectDirectory) ? null : _projectDirectory;
            }
        }

        public string ProjectFile
        {
            get
            {
                if (!_checkedProjectFile && _vsProject != null)
                {
                    _checkedProjectFile = true;
                    string name;

                    if (ErrorHandler.Succeeded(_vsProject.GetMkDocument(VSConstants.VSITEMID_ROOT, out name)))
                    {
                        if(SvnItem.IsValidPath(name))
                            _projectFile = name;                        
                    }
                }
                return _projectFile;
            }
        }

        string _uniqueName;
        public string UniqueProjectName
        {
            get
            {
                if (_uniqueName != null)
                    return _uniqueName;
                IVsSolution3 solution = _context.GetService<IVsSolution3>(typeof(SVsSolution));

                if (solution != null)
                {
                    string name;

                    if (ErrorHandler.Succeeded(solution.GetUniqueUINameOfProject(ProjectHierarchy, out name)))
                        _uniqueName = name;
                }
                return _uniqueName ?? ProjectName;
            }
        }

        public SvnProject SvnProject
        {
            get
            {
                if (_svnProjectInstance == null)
                    _svnProjectInstance = new SvnProject(ProjectFile, SccProject);

                return _svnProjectInstance;
            }
        }

        /// <summary>
        /// Checks whether the project might have been renamed
        /// </summary>
        /// <param name="project">The project.</param>
        /// <param name="oldName">The old name.</param>
        /// <param name="newName">The new name.</param>
        internal void CheckProjectRename(IVsSccProject2 project, string oldName, string newName)
        {
            if (_checkedProjectFile && oldName == ProjectFile)
            {
                _checkedProjectFile = false;
                _projectFile = null;
                _svnProjectInstance = null;
            }
        }

        /// <summary>
        /// Gets a boolean indicating whether the project thinks its registered on the scc provider
        /// </summary>
        /// <remarks>
        /// True if the project registered itself on the Scc provider, otherwise false
        /// </remarks>
        public bool IsRegistered
        {
            get { return _isRegistered; }

            internal set { _isRegistered = value; }
        }

        public bool IsSolutionFolder
        {
            get { return _projectType == SccProjectType.SolutionFolder; }
        }

        public bool IsWebSite
        {
            get { return _projectType == SccProjectType.WebSite; }
        }

        internal void SetManaged(bool managed)
        {
            if (managed == IsManaged)
                return;

            if (managed)
                Marshal.ThrowExceptionForHR(SccProject.SetSccLocation("Svn", "Svn", "Svn", AnkhId.SubversionSccName));
            else
            {
                // The managed package framework assumes empty strings for clearing; null will fail there
                Marshal.ThrowExceptionForHR(SccProject.SetSccLocation("", "", "", ""));
            }

            IsManaged = managed;
        }

        internal void OnClose()
        {
            Hook(false);
            while (_files.Count > 0)
            {
                SccProjectFileReference r = _files[0];
                while (r.ReferenceCount > 0)
                {
                    r.ReleaseReference();
                }
            }
            _loaded = false;
        }

        bool _disposed;
        internal void Dispose()
        {
            _disposed = true;
            Hook(false);            
        }

        public bool IsDisposed
        {
            get { return _disposed; }
        }

        bool _inLoad;
        internal void Load()
        {
            if (_loaded)
                return;

            _inLoad = true;
            try
            {
                Debug.Assert(_files.Count == 0);

                _checkedProjectFile = false;
                _projectFile = null;
                _svnProjectInstance = null;                
                _loaded = true;

                ISccProjectWalker walker = _context.GetService<ISccProjectWalker>();

                if (walker != null)
                {
                    Dictionary<string, uint> ids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in walker.GetSccFiles(ProjectHierarchy, VSConstants.VSITEMID_ROOT, ProjectWalkDepth.AllDescendantsInHierarchy, ids))
                    {
                        AddPath(file, ids); // GetSccFiles returns normalized paths
                    }
                }

                IFileStatusMonitor monitor = _context.GetService<IFileStatusMonitor>();
                if (monitor != null)
                {
                    // Make sure we see all files as possible pending changes
                    monitor.ScheduleGlyphUpdate(GetAllFiles());
                }
                Hook(true); 
            }
            finally
            {
                _inLoad = false;
            }
        }        

        internal void Reload()
        {
            _projectName = null;
            _uniqueName = null;
            OnClose();
            Load();
        }

        internal bool TrackProjectChanges()
        {
            return _loaded && !_inLoad;
        }

        public IEnumerable<string> GetAllFiles()
        {
            foreach (SccProjectFileReference r in _files)
            {
                yield return r.Filename;
            }
        }

        internal void AddPath(string path)
        {            
            if (_loaded && IsWebSite)
            {                
                uint fid = VSConstants.VSITEMID_NIL;

                if (TryGetProjectFileId(path, out fid))
                {
                    // OK: Websites try to add everything they see below their directory
                    // We only add these files if they are actually SCC items

                    bool add = false;
                    ISccProjectWalker walker = _context.GetService<ISccProjectWalker>();

                    foreach (string file in walker.GetSccFiles(ProjectHierarchy, fid, ProjectWalkDepth.SpecialFiles, null))
                    {
                        if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
                        {
                            add = true;
                            break;
                        }
                    }

                    if (!add)
                        return;
                }
                else
                {
                    // File is either not in project or a scc special file
                    // Pass
                }
            }


            if (_files.Contains(path))
                _files[path].AddReference();
            else
                _files.Add(new SccProjectFileReference(_context, this, Scc.GetFile(path)));

            if (!_inLoad && _loaded && !string.IsNullOrEmpty(ProjectFile))
            {
                ISccProjectWalker walker = _context.GetService<ISccProjectWalker>();

                if(walker != null)
                    walker.SetPrecreatedFilterItem(null, VSConstants.VSITEMID_NIL);
                
                ClearIdCache();

                SetDirty();
            }
        }

        private void AddPath(string path, Dictionary<string, uint> ids)
        {
            AddPath(path);

            uint id;
            if (ids != null && ids.TryGetValue(path, out id))
            {
                if(_files.Contains(path))
                    _files[path].SetId(id);
            }
        }

        internal void RemovePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException("path");

            if (!_files.Contains(path))
                return;

            _files[path].ReleaseReference();

            ClearIdCache();

            if (!_inLoad && _loaded && !string.IsNullOrEmpty(ProjectFile))
            {
                SetDirty();
            }
        }

        #region File list management code
        internal void InvokeRemoveReference(SccProjectFileReference sccProjectFileReference)
        {
            _files.Remove(sccProjectFileReference);
        }
        #endregion


        #region Helper code

        AnkhSccProvider Scc
        {
            get { return _scc ?? (_scc = _context.GetService<AnkhSccProvider>()); }
        }

        /// <summary>
        /// Checks whether the specified project is a solution folder
        /// </summary>
        private static readonly Guid _solutionFolderProjectId = new Guid("2150e333-8fdc-42a3-9474-1a3956d46de8");
        private static readonly Guid _websiteProjectId = new Guid("e24c65dc-7377-472b-9aba-bc803b73c61a");
        static SccProjectType GetProjectType(IVsSccProject2 project)
        {
            IPersistFileFormat pFileFormat = project as IPersistFileFormat;
            if (pFileFormat != null)
            {
                Guid guidClassID;
                if (VSConstants.S_OK != pFileFormat.GetClassID(out guidClassID))
                    return SccProjectType.Normal;

                if (guidClassID == _solutionFolderProjectId)
                    return SccProjectType.SolutionFolder;
                else if (guidClassID == _websiteProjectId)
                    return SccProjectType.WebSite;
            }

            return SccProjectType.Normal;
        }
        #endregion                
    
        /// <summary>
        /// Determines whether the project contains the specified file
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>
        /// 	<c>true</c> if the project contains the file; otherwise, <c>false</c>.
        /// </returns>
        internal bool ContainsFile(string path)
        {
            return _files.Contains(path);
        }

        void ClearIdCache()
        {
            foreach (SccProjectFileReference r in _files)
            {
                r.ClearIdCache();
            }
        }

        bool _fetchedImgList;
        IntPtr _projectImgList;
        internal IntPtr ProjectImageList
        {
            get
            {
                if (_fetchedImgList)
                    return _projectImgList;

                _fetchedImgList = true;
                object value;
                if (ErrorHandler.Succeeded(ProjectHierarchy.GetProperty(VSConstants.VSITEMID_ROOT, 
                    (int)__VSHPROPID.VSHPROPID_IconImgList, out value)))
                {
                    _projectImgList = (IntPtr)(int)value; // Marshalled by VS as 32 bit integer
                }

                return _projectImgList;
            }
        }

        /// <summary>
        /// Tries to get the hierarchy id of a file within a project
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="itemId">The item id.</param>
        /// <returns></returns>
        public bool TryGetProjectFileId(string path, out uint itemId)
        {
            int found;
            uint id;
            VSDOCUMENTPRIORITY[] prio = new VSDOCUMENTPRIORITY[1];

            if(ErrorHandler.Succeeded(
                VsProject.IsDocumentInProject(path, out found, prio, out id)))
            {
                // Priority also returns information on whether the file can be added
                if(found != 0 && prio[0] >= VSDOCUMENTPRIORITY.DP_Standard && id != 0)
                {
                    itemId = id;
                    return true;
                }
            }

            itemId = VSConstants.VSITEMID_NIL;
            return false;
        }
    }
}
