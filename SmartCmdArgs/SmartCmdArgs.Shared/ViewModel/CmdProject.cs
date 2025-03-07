using SmartCmdArgs.Helper;
using System;
using System.Collections.Generic;

namespace SmartCmdArgs.ViewModel
{
    public class CmdProject : CmdContainer
    {
        private TreeViewModel _parentTreeViewModel;
        public TreeViewModel ParentTreeViewModel
        {
            get => _parentTreeViewModel;
            set
            {
                if (value == null)
                    GetEnumerable(includeSelf: true).ForEach(i => i.IsSelected = false);
                _parentTreeViewModel = value;
            }
        }

        private bool isStartupProject = false;
        public bool IsStartupProject
        {
            get => isStartupProject; set
            {
                // this is taken care of by the code who sets 'IsStartupProject' to increase performance
                //if (value != isStartupProject)
                //    ParentTreeViewModel.UpdateTree();

                SetAndNotify(value, ref isStartupProject);
            }
        }

        public override bool IsSelected
        {
            get => isSelected;
            set
            {
                SetAndNotify(value, ref isSelected);
                ParentTreeViewModel?.OnItemSelectionChanged(this);
            }
        }
        private bool isHiddenInList = false;
        public bool HiddenInList 
        {
            get => isHiddenInList;
            set
            {
                bool oldValue = isHiddenInList;
                SetAndNotify(value, ref isHiddenInList, nameof(isHiddenInList));
                if (oldValue != value)
                {
                    BubbleEvent(new IsHiddenChangedEvent(this, oldValue, value), null);
                }
            }
            
        }

        protected override Predicate<CmdBase> FilterPredicate => Filter;
        private Predicate<CmdBase> filter;
        public Predicate<CmdBase> Filter
        {
            get => filter; set { filter = value; RefreshFilters(); }
        }

        public override Guid ProjectGuid => Id;

        public Guid Kind { get; set; }
        public new string ProjectPlatform
        {
            get => base.ProjectPlatform;
            set => base.ProjectPlatform = value;
        }
        public new string ProjectConfig
        {
            get => base.ProjectConfig;
            set => base.ProjectConfig = value;
        }

        public CmdProject(Guid id, Guid kind, string displayName, IEnumerable<CmdBase> items, bool isExpanded, bool exclusiveMode, string projConf, string projPlatform, string delimiter, string prefix, string postfix, bool hiddenInList)
            : base(id, displayName, items, isExpanded, exclusiveMode, delimiter, prefix, postfix)
        {
            Kind = kind;
            HiddenInList = hiddenInList;
            ProjectConfig = projConf;
            ProjectPlatform = projPlatform;
        }

        public override CmdBase Copy()
        {
            throw new InvalidOperationException("Can't copy a project");
        }

        protected override void BubbleEvent(TreeEventBase treeEvent, CmdBase receiver)
        {
            if (treeEvent != null)
                treeEvent.AffectedProject = this;

            ParentTreeViewModel?.OnTreeEvent(treeEvent);
        }

        public bool NeedsSaving()
		{
            return (Items != null && Items.Count != 0) || HiddenInList || ExclusiveMode
                || !String.IsNullOrEmpty( Prefix ) || !String.IsNullOrEmpty( Postfix ) || !String.IsNullOrEmpty( Delimiter )
                || !String.IsNullOrEmpty( ProjectPlatform ) || !String.IsNullOrEmpty( ProjectConfig );
        }
    }
}
