using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.VFX;
using UnityEditor.Experimental;
using UnityEditor.Experimental.VFX;
using UnityEditor.Experimental.Graph;

using Object = UnityEngine.Object;

namespace UnityEditor.Experimental
{
    internal class VFXEdContextNode : VFXEdNode, VFXModelHolder
    {

        public VFXEdContextNodeBlock ContextNodeBlock
        {
            get { return m_ContextNodeBlock; }
            set
            {
                if (m_ContextNodeBlock != null)
                    RemoveChild(m_ContextNodeBlock);
                m_ContextNodeBlock = value;
                if (m_ContextNodeBlock != null)
                    AddChild(m_ContextNodeBlock);
            }
        }
        private VFXEdContextNodeBlock m_ContextNodeBlock;

        public VFXContextModel Model    { get { return m_Model; } }
        public VFXContextDesc Desc      { get { return Model.Desc; } }
        public VFXContextDesc.Type Context     { get { return Desc.m_Type; } }

        public VFXElementModel GetAbstractModel() { return Model; }

        protected VFXContextModel m_Model;
        private Rect m_RefSpaceButtonRect;


        internal VFXEdContextNode(VFXContextModel model, VFXEdDataSource dataSource) 
            : base(model.UIPosition,dataSource)
        {
            m_Model = model;
            collapsed = model.UICollapsed;

            m_Title = VFXContextDesc.GetTypeName(Context);
            target = ScriptableObject.CreateInstance<VFXEdContextNodeTarget>();
            (target as VFXEdContextNodeTarget).targetNode = this;

            SetContext(Desc);

            m_Inputs.Add(new VFXEdFlowAnchor(1, typeof(float), Context, m_DataSource, Direction.Input));
            m_Outputs.Add(new VFXEdFlowAnchor(2, typeof(float), Context, m_DataSource, Direction.Output));

            AddChild(inputs[0]);

            AddManipulator(new TooltipManipulator(GetTooltipText));

            // Add stuff for Output Nodes
            if (model.Desc.m_Type == VFXContextDesc.Type.kTypeOutput)
            {
                AddChild(new VFXEdDisableOutputOption(m_Model));
            }
            else
            {
                AddChild(outputs[0]);
            }

            MouseDown += ManageRefSpaceButtonClick;

            ZSort();
            Layout();
        }

        private bool ManageRefSpaceButtonClick(CanvasElement element, Event e, Canvas2D parent)
        {
            Rect canvasButtonRect = new Rect(m_RefSpaceButtonRect.x + element.translation.x, m_RefSpaceButtonRect.y + element.translation.y, m_RefSpaceButtonRect.width, m_RefSpaceButtonRect.height);
            if (canvasButtonRect.Contains(parent.MouseToCanvas(e.mousePosition)))
            {
                var system = Model.GetOwner();
                system.WorldSpace = !system.WorldSpace;
                m_DataSource.SyncView(system, true);
                e.Use();
                return true;
            }
            return false;
        }

        protected virtual List<string> GetTooltipText()
        {
            List<string> lines = new List<string>();
            lines = VFXModelDebugInfoProvider.GetInfo(lines, Model, VFXModelDebugInfoProvider.InfoFlag.kDefault);
            return lines;
        }

        public void SetSlotValue(string name, VFXValue value)
        {
            VFXContextModel model = ContextNodeBlock.Model;
            for(int i = 0; i < model.GetNbSlots(); i++)
            {
                if (model.Desc.m_Properties[i].m_Name == name)
                {
                    model.GetSlot(i).Value = value; 
                }
            }
        }

        private static string FormatMenuString(VFXBlockDesc block)
        {
            return block.Category + (block.Category.Length != 0 ? "/" : "") + block.Name;
        }

        protected override MiniMenu.MenuSet GetNodeMenu(Vector2 mousePosition)
        {

            MiniMenu.MenuSet menu = new MiniMenu.MenuSet();
            menu.AddMenuEntry("NodeBlock", "Add New...", AddNodeBlock, null);

            int index = m_NodeBlockContainer.GetBlockIndex(ParentCanvas().MouseToCanvas(mousePosition));
            if(index >= 0)
            {
                VFXBlockModel targetBlock = Model.GetChild(index);

                if( targetBlock != null)
                {
                    menu.AddMenuEntry("NodeBlock", "Replace by...", ReplaceNodeBlock, targetBlock);
                    menu.AddMenuEntry("NodeBlock", targetBlock.Enabled ? "Disable" : "Enable", ToggleNodeBlockEnabled, targetBlock);
                }
            }


            

            foreach(VFXContextDesc desc in VFXEditor.ContextLibrary.GetContexts())
            {
                if(desc.m_Type == Model.Desc.m_Type && desc != Model.Desc)
                        menu.AddMenuEntry("Switch Context", desc.Name, SwitchContext, desc);
            }

            menu.AddMenuEntry("Layout", "Layout System", LayoutSystem, null);
            menu.AddMenuEntry("Layout", "Collapse All", CollapseAll, null);
            menu.AddMenuEntry("Layout", "Expand All", ExpandAll, null);
            menu.AddMenuEntry("Layout", "Collapse UnConnected", CollapseUnconnected, null);
            menu.AddMenuEntry("Layout", "Collapse Connected", CollapseConnected, null);
            return menu;
        }

        public void AddNodeBlock(Vector2 position, object o)
        {
            VFXFilterPopup.ShowNewBlockPopup(this, position, ParentCanvas(), true);
        }

        public void ReplaceNodeBlock(Vector2 position, object o)
        {
            VFXBlockModel block = o as VFXBlockModel;
            VFXFilterPopup.ShowReplaceBlockPopup(this, block, position, ParentCanvas(), true);
        }

        public void ToggleNodeBlockEnabled(Vector2 position, object o)
        {
            var block = (VFXEdProcessingNodeBlock)o;
            block.Model.Enabled = !block.Model.Enabled;
            m_DataSource.SyncView(block.Model);
        }

        public void SwitchContext(Vector2 position, object o)
        {
            SetContext(o as VFXContextDesc);
        }

        public void SetContext(VFXContextDesc context)
        {
            if (ContextNodeBlock != null)
            {
                for (int i = 0; i < Model.GetNbSlots(); i++)
                    Model.GetSlot(i).UnlinkRecursively();
                m_DataSource.SyncView(Model, true); 
            }

            Model.Desc = context;

            if (m_Model.Desc.ShowBlock)
                ContextNodeBlock = new VFXEdContextNodeBlock(m_DataSource, m_Model);
            else
            {
                if (ContextNodeBlock != null)
                {
                    ContextNodeBlock = null;    
                }               
            }

            Layout();
            Invalidate();
            var canvas = ParentCanvas();
            if (canvas != null)
            {
                canvas.ReloadData();
                canvas.Repaint();
            }
        }


        public void LayoutSystem(Vector2 position, object o)
        {
            VFXEdLayoutUtility.LayoutSystem(Model.GetOwner(),m_DataSource);
        }

        public void CollapseUnconnected(Vector2 position, object o)
        {
            foreach(VFXEdNodeBlock block in NodeBlockContainer.nodeBlocks)
            {
                block.collapsed = !block.IsConnected();
            }
            NodeBlockContainer.Resync();
        }

        public void CollapseConnected(Vector2 position, object o)
        {
            foreach(VFXEdNodeBlock block in NodeBlockContainer.nodeBlocks)
            {
                block.collapsed = block.IsConnected();
            }
            NodeBlockContainer.Resync();
        }

        public void CollapseAll(Vector2 position, object o)
        {
            foreach(VFXEdNodeBlock block in NodeBlockContainer.nodeBlocks)
            {
                block.collapsed = true;
            }
            NodeBlockContainer.Resync();
        }

        public void ExpandAll(Vector2 position, object o)
        {
            foreach(VFXEdNodeBlock block in NodeBlockContainer.nodeBlocks)
            {
                block.collapsed = false;
            }
            NodeBlockContainer.Resync();
        }


        public override void OnAddNodeBlock(VFXEdNodeBlock nodeblock, int index)
        {
            //Model.AddChild((nodeblock as VFXEdProcessingNodeBlock).Model,index);
        }

        public override bool AcceptNodeBlock(VFXEdNodeBlockDraggable block)
        {
            return Model.CanAddChild(block.GetAbstractModel());
        }

        public override void Layout()
        {
            if (m_ContextNodeBlock != null)
                m_HeaderOffset = m_ContextNodeBlock.GetHeight();
            else
                m_HeaderOffset = 0.0f;

            base.Layout();

            if (m_ContextNodeBlock != null)
            {
                m_ContextNodeBlock.translation = m_ClientArea.position + VFXEditorMetrics.NodeBlockContainerPosition;
                m_ContextNodeBlock.scale = new Vector2(m_NodeBlockContainer.scale.x, m_ContextNodeBlock.GetHeight());
            }

            // Position RefSpace Button
            m_RefSpaceButtonRect = new Rect(scale.x - 84, 24, 48, 24);

        }

        public override void Render(Rect parentRect, Canvas2D canvas)
        {
            Rect r = m_ClientArea;

            if(parent is VFXEdCanvas) {

                Color c =  VFXEditor.styles.GetContextColor(Context);
                float a = 0.7f;
                GUI.color = new Color(c.r/a, c.g/a, c.b/a, a);
                GUI.Box(VFXEditorMetrics.NodeImplicitContextOffset.Add(new Rect(0, 0, scale.x, scale.y)), "", VFXEditor.styles.Context);
                GUI.color = new Color(1.0f, 1.0f, 1.0f, 1.0f);
            }
           
            GUI.Box(r, "", VFXEditor.styles.Node);
            GUI.Label(new Rect(0, r.y, r.width, 24), title, VFXEditor.styles.NodeTitle);

            base.Render(parentRect, canvas);

            bool world =  Model.GetOwner().WorldSpace;
            GUI.Box(m_RefSpaceButtonRect, world ? "World" : "Local", world ? VFXEditor.styles.RefSpaceButtonWorld : VFXEditor.styles.RefSpaceButtonLocal);

        }

        public override void UpdateModel(UpdateType t)
        {
            Model.UpdateCollapsed(collapsed);
            Model.UpdatePosition(translation);
        }
    }
}
