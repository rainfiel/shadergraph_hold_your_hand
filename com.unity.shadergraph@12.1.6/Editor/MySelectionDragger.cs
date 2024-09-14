using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityEditor.Experimental.GraphView
{
    //
    // 摘要:
    //     Selection dragger manipulator.
    public class MySelectionDragger : Dragger
    {
        private class OriginalPos
        {
            public Rect pos;

            public Scope scope;

            public StackNode stack;

            public int stackIndex;

            public bool dragStarted;
        }

        private IDropTarget m_PrevDropTarget;

        private bool m_ShiftClicked = false;

        private bool m_Dragging = false;

        //  private Snapper m_Snapper = new Snapper();

        private GraphViewChange m_GraphViewChange;

        private List<GraphElement> m_MovedElements;

        private List<VisualElement> m_DropTargetPickList = new List<VisualElement>();

        private GraphView m_GraphView;

        private Dictionary<GraphElement, OriginalPos> m_OriginalPos;

        private Vector2 m_originalMouse;

        internal const int k_PanAreaWidth = 100;

        internal const int k_PanSpeed = 4;

        internal const int k_PanInterval = 10;

        internal const float k_MinSpeedFactor = 0.5f;

        internal const float k_MaxSpeedFactor = 2.5f;

        internal const float k_MaxPanSpeed = 10f;

        private IVisualElementScheduledItem m_PanSchedule;

        private Vector3 m_PanDiff = Vector3.zero;

        private Vector3 m_ItemPanDiff = Vector3.zero;

        private Vector2 m_MouseDiff = Vector2.zero;

        private float m_XScale;

        internal bool snapEnabled { get; set; }

        private GraphElement selectedElement { get; set; }

        private GraphElement clickedElement { get; set; }

        private IDropTarget GetDropTargetAt(Vector2 mousePosition, IEnumerable<VisualElement> exclusionList)
        {
            List<VisualElement> dropTargetPickList = m_DropTargetPickList;
            dropTargetPickList.Clear();
            base.target.panel.PickAll(mousePosition, dropTargetPickList);
            IDropTarget dropTarget = null;
            for (int i = 0; i < dropTargetPickList.Count; i++)
            {
                if (dropTargetPickList[i] == base.target && base.target != m_GraphView)
                {
                    continue;
                }

                VisualElement visualElement = dropTargetPickList[i];
                dropTarget = visualElement as IDropTarget;
                if (dropTarget != null)
                {
                    if (!exclusionList.Contains(visualElement))
                    {
                        break;
                    }

                    dropTarget = null;
                }
            }

            return dropTarget;
        }

        //
        // 摘要:
        //     SelectionDragger's constructor.
        public MySelectionDragger()
        {
            snapEnabled = false; // EditorPrefs.GetBool("GraphSnapping", defaultValue: true);
            base.activators.Add(new ManipulatorActivationFilter
            {
                button = MouseButton.LeftMouse
            });
            base.activators.Add(new ManipulatorActivationFilter
            {
                button = MouseButton.LeftMouse,
                modifiers = EventModifiers.Shift
            });
            if (Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer)
            {
                base.activators.Add(new ManipulatorActivationFilter
                {
                    button = MouseButton.LeftMouse,
                    modifiers = EventModifiers.Command
                });
            }
            else
            {
                base.activators.Add(new ManipulatorActivationFilter
                {
                    button = MouseButton.LeftMouse,
                    modifiers = EventModifiers.Control
                });
            }

            base.panSpeed = new Vector2(1f, 1f);
            base.clampToParentEdges = false;
            m_MovedElements = new List<GraphElement>();
            m_GraphViewChange.movedElements = m_MovedElements;
        }

        //
        // 摘要:
        //     Called to register click event callbacks on the target element.
        protected override void RegisterCallbacksOnTarget()
        {
            if (!(base.target is ISelection))
            {
                throw new InvalidOperationException("Manipulator can only be added to a control that supports selection");
            }

            base.target.RegisterCallback<MouseDownEvent>(OnMouseDown);
            base.target.RegisterCallback<MouseMoveEvent>(OnMouseMove);
            base.target.RegisterCallback<MouseUpEvent>(OnMouseUp);
            base.target.RegisterCallback<KeyDownEvent>(OnKeyDown);
            base.target.RegisterCallback<MouseCaptureOutEvent>(OnMouseCaptureOutEvent);
            m_Dragging = false;
        }

        //
        // 摘要:
        //     Called to unregister event callbacks from the target element.
        protected override void UnregisterCallbacksFromTarget()
        {
            base.target.UnregisterCallback<MouseDownEvent>(OnMouseDown);
            base.target.UnregisterCallback<MouseMoveEvent>(OnMouseMove);
            base.target.UnregisterCallback<MouseUpEvent>(OnMouseUp);
            base.target.UnregisterCallback<KeyDownEvent>(OnKeyDown);
            base.target.UnregisterCallback<MouseCaptureOutEvent>(OnMouseCaptureOutEvent);
        }

        private static void SendDragAndDropEvent(IDragAndDropEvent evt, List<ISelectable> selection, IDropTarget dropTarget, ISelection dragSource)
        {
            if (dropTarget == null)
            {
                return;
            }

            EventBase eventBase = evt as EventBase;
            if (eventBase.eventTypeId == EventBase<DragExitedEvent>.TypeId())
            {
                dropTarget.DragExited();
            }
            else if (eventBase.eventTypeId == EventBase<DragEnterEvent>.TypeId())
            {
                dropTarget.DragEnter(evt as DragEnterEvent, selection, dropTarget, dragSource);
            }
            else if (eventBase.eventTypeId == EventBase<DragLeaveEvent>.TypeId())
            {
                dropTarget.DragLeave(evt as DragLeaveEvent, selection, dropTarget, dragSource);
            }

            if (dropTarget.CanAcceptDrop(selection))
            {
                if (eventBase.eventTypeId == EventBase<DragPerformEvent>.TypeId())
                {
                    dropTarget.DragPerform(evt as DragPerformEvent, selection, dropTarget, dragSource);
                }
                else if (eventBase.eventTypeId == EventBase<DragUpdatedEvent>.TypeId())
                {
                    dropTarget.DragUpdated(evt as DragUpdatedEvent, selection, dropTarget, dragSource);
                }
            }
        }

        private void OnMouseCaptureOutEvent(MouseCaptureOutEvent e)
        {
            if (m_Active)
            {
                if (m_PrevDropTarget != null && m_GraphView != null && m_PrevDropTarget.CanAcceptDrop(m_GraphView.selection))
                {
                    m_PrevDropTarget.DragExited();
                }

                selectedElement = null;
                m_PrevDropTarget = null;
                m_Active = false;
                if (snapEnabled)
                {
                    //         m_Snapper.EndSnap(m_GraphView);
                }
            }
        }

        //
        // 摘要:
        //     Called on mouse down event.
        //
        // 参数:
        //   e:
        //     The event.
        protected new void OnMouseDown(MouseDownEvent e)
        {
            if (m_Active)
            {
                e.StopImmediatePropagation();
            }
            else
            {
                if (!CanStartManipulation(e))
                {
                    return;
                }

                m_GraphView = base.target as GraphView;
                if (m_GraphView == null)
                {
                    return;
                }

                selectedElement = null;
                clickedElement = e.target as GraphElement;
                if (clickedElement == null)
                {
                    VisualElement visualElement = e.target as VisualElement;
                    clickedElement = visualElement.GetFirstAncestorOfType<GraphElement>();
                    if (clickedElement == null)
                    {
                        return;
                    }
                }

                if (!clickedElement.IsMovable() || !clickedElement.HitTest(clickedElement.WorldToLocal(e.mousePosition)))
                {
                    return;
                }

                if (!m_GraphView.selection.Contains(clickedElement))
                {
                    e.StopImmediatePropagation();
                    return;
                }

                selectedElement = clickedElement;
                m_OriginalPos = new Dictionary<GraphElement, OriginalPos>();
                HashSet<GraphElement> hashSet = new HashSet<GraphElement>(m_GraphView.selection.OfType<GraphElement>());
                HashSet<Placemat> hashSet2 = new HashSet<Placemat>(hashSet.OfType<Placemat>());
                foreach (Placemat item in hashSet2)
                {
             //       item.GetElementsToMove(e.shiftKey, hashSet);
                }

                foreach (GraphElement item2 in hashSet)
                {
                    if (item2 == null || !item2.IsMovable())
                    {
                        continue;
                    }

                    StackNode stackNode = null;
                    if (item2.parent is StackNode)
                    {
                        stackNode = item2.parent as StackNode;
                        if (stackNode.IsSelected(m_GraphView))
                        {
                            continue;
                        }
                    }

                    Rect position = item2.GetPosition();
                    Rect pos = item2.hierarchy.parent.ChangeCoordinatesTo(m_GraphView.contentViewContainer, position);
                    m_OriginalPos[item2] = new OriginalPos
                    {
                        pos = pos,
                        scope = item2.GetContainingScope(),
                        stack = stackNode,
                        stackIndex = (stackNode?.IndexOf(item2) ?? (-1))
                    };
                }

                m_originalMouse = e.mousePosition;
                m_ItemPanDiff = Vector3.zero;
                if (m_PanSchedule == null)
                {
                    m_PanSchedule = m_GraphView.schedule.Execute(Pan).Every(10L).StartingIn(10L);
                    m_PanSchedule.Pause();
                }

                if ((selectedElement.capabilities & Capabilities.Snappable) == 0)
                {
                    snapEnabled = false;
                }
                else
                {
                    //   snapEnabled = EditorPrefs.GetBool("GraphSnapping", defaultValue: true);
                }

                if (snapEnabled)
                {
                    //       m_Snapper.BeginSnap(m_GraphView);
                }

                m_Active = true;
                base.target.CaptureMouse();
                e.StopImmediatePropagation();
            }
        }

        internal Vector2 GetEffectivePanSpeed(Vector2 mousePos)
        {
            Vector2 zero = Vector2.zero;
            if (mousePos.x <= 100f)
            {
                zero.x = (0f - ((100f - mousePos.x) / 100f + 0.5f)) * 4f;
            }
            else if (mousePos.x >= m_GraphView.contentContainer.layout.width - 100f)
            {
                zero.x = ((mousePos.x - (m_GraphView.contentContainer.layout.width - 100f)) / 100f + 0.5f) * 4f;
            }

            if (mousePos.y <= 100f)
            {
                zero.y = (0f - ((100f - mousePos.y) / 100f + 0.5f)) * 4f;
            }
            else if (mousePos.y >= m_GraphView.contentContainer.layout.height - 100f)
            {
                zero.y = ((mousePos.y - (m_GraphView.contentContainer.layout.height - 100f)) / 100f + 0.5f) * 4f;
            }

            return Vector2.ClampMagnitude(zero, 10f);
        }

        private void ComputeSnappedRect(ref Rect selectedElementProposedGeom, float scale)
        {
            //    Rect sourceRect = selectedElement.parent.ChangeCoordinatesTo(m_GraphView.contentViewContainer, selectedElementProposedGeom);
            //    sourceRect = m_Snapper.GetSnappedRect(sourceRect, scale);
            //    selectedElementProposedGeom = m_GraphView.contentViewContainer.ChangeCoordinatesTo(selectedElement.parent, sourceRect);
        }

        //
        // 摘要:
        //     Called on mouse move event.
        //
        // 参数:
        //   e:
        //     The event.
        protected new void OnMouseMove(MouseMoveEvent e)
        {
            if (!m_Active || m_GraphView == null)
            {
                return;
            }

            VisualElement src = (VisualElement)e.target;
            Vector2 mousePos = src.ChangeCoordinatesTo(m_GraphView.contentContainer, e.localMousePosition);
            m_PanDiff = GetEffectivePanSpeed(mousePos);
            if (m_PanDiff != Vector3.zero)
            {
                m_PanSchedule.Resume();
            }
            else
            {
                m_PanSchedule.Pause();
            }

            m_MouseDiff = m_originalMouse - e.mousePosition;
            Dictionary<Group, List<GraphElement>> dictionary = (e.shiftKey ? new Dictionary<Group, List<GraphElement>>() : null);
            Rect selectedElementProposedGeom = GetSelectedElementGeom();
            m_ShiftClicked = e.shiftKey;
            if (snapEnabled && !m_ShiftClicked)
            {
                ComputeSnappedRect(ref selectedElementProposedGeom, m_XScale);
            }

            if (snapEnabled && m_ShiftClicked)
            {
                //       m_Snapper.ClearSnapLines();
            }

            foreach (KeyValuePair<GraphElement, OriginalPos> originalPo in m_OriginalPos)
            {
                GraphElement key = originalPo.Key;
                if (key.hierarchy.parent == null)
                {
                    continue;
                }

                if (!originalPo.Value.dragStarted)
                {
                    key.GetFirstAncestorOfType<StackNode>()?.OnStartDragging(key);
                    if (dictionary != null && key.GetContainingScope() is Group key2)
                    {
                        if (!dictionary.ContainsKey(key2))
                        {
                            dictionary[key2] = new List<GraphElement>();
                        }

                        dictionary[key2].Add(key);
                    }

                    originalPo.Value.dragStarted = true;
                }

                SnapOrMoveElement(originalPo, selectedElementProposedGeom);
            }

            if (dictionary != null)
            {
                foreach (KeyValuePair<Group, List<GraphElement>> item in dictionary)
                {
                 //   item.Key.OnStartDragging(e, item.Value);
                }
            }

            List<ISelectable> selection = m_GraphView.selection;
            IDropTarget dropTargetAt = GetDropTargetAt(e.mousePosition, selection.OfType<VisualElement>());
            if (m_PrevDropTarget != dropTargetAt)
            {
                if (m_PrevDropTarget != null)
                {
                    using DragLeaveEvent evt = MouseEventBase<DragLeaveEvent>.GetPooled(e);
                    SendDragAndDropEvent(evt, selection, m_PrevDropTarget, m_GraphView);
                }

                using DragEnterEvent evt2 = MouseEventBase<DragEnterEvent>.GetPooled(e);
                SendDragAndDropEvent(evt2, selection, dropTargetAt, m_GraphView);
            }

            using (DragUpdatedEvent evt3 = MouseEventBase<DragUpdatedEvent>.GetPooled(e))
            {
                SendDragAndDropEvent(evt3, selection, dropTargetAt, m_GraphView);
            }

            m_PrevDropTarget = dropTargetAt;
            m_Dragging = true;
            e.StopPropagation();
        }

        private void Pan(TimerState ts)
        {
            m_GraphView.viewTransform.position -= m_PanDiff;
            m_ItemPanDiff += m_PanDiff;
            Rect selectedElementProposedGeom = GetSelectedElementGeom();
            if (snapEnabled && !m_ShiftClicked)
            {
                ComputeSnappedRect(ref selectedElementProposedGeom, m_XScale);
            }

            foreach (KeyValuePair<GraphElement, OriginalPos> originalPo in m_OriginalPos)
            {
                SnapOrMoveElement(originalPo, selectedElementProposedGeom);
            }
        }

        private void SnapOrMoveElement(KeyValuePair<GraphElement, OriginalPos> v, Rect selectedElementGeom)
        {
            GraphElement key = v.Key;
            /*       if (EditorPrefs.GetBool("GraphSnapping"))
                   {
                       Vector2 vector = selectedElementGeom.position - m_OriginalPos[selectedElement].pos.position;
                       Rect position = key.GetPosition();
                       key.SetPosition(new Rect(v.Value.pos.x + vector.x, v.Value.pos.y + vector.y, position.width, position.height));
                   }
                   else*/
            {
                MoveElement(key, v.Value.pos);
            }
        }

        private Rect GetSelectedElementGeom()
        {
            m_XScale = selectedElement.worldTransform.m00;
            Rect pos = m_OriginalPos[selectedElement].pos;
            pos.x -= (m_MouseDiff.x - m_ItemPanDiff.x) * base.panSpeed.x / m_XScale;
            pos.y -= (m_MouseDiff.y - m_ItemPanDiff.y) * base.panSpeed.y / m_XScale;
            return pos;
        }

        private void MoveElement(GraphElement element, Rect originalPos)
        {
            Matrix4x4 worldTransform = element.worldTransform;
            Vector3 vector = new Vector3(worldTransform.m00, worldTransform.m11, worldTransform.m22);
            Rect rect = new Rect(0f, 0f, originalPos.width, originalPos.height);
            rect.x = originalPos.x - (m_MouseDiff.x - m_ItemPanDiff.x) * base.panSpeed.x / vector.x * element.transform.scale.x;
            rect.y = originalPos.y - (m_MouseDiff.y - m_ItemPanDiff.y) * base.panSpeed.y / vector.y * element.transform.scale.y;
            element.SetPosition(m_GraphView.contentViewContainer.ChangeCoordinatesTo(element.hierarchy.parent, rect));
        }

        //
        // 摘要:
        //     Called on mouse up event.
        //
        // 参数:
        //   e:
        //     The event.
        //
        //   evt:
        protected new void OnMouseUp(MouseUpEvent evt)
        {
            if (m_GraphView == null)
            {
                if (m_Active)
                {
                    base.target.ReleaseMouse();
                    selectedElement = null;
                    m_Active = false;
                    m_Dragging = false;
                    m_PrevDropTarget = null;
                }

                return;
            }

            List<ISelectable> selection = m_GraphView.selection;
            if (!CanStopManipulation(evt))
            {
                return;
            }

            if (m_Active)
            {
                if (m_Dragging)
                {
                    foreach (IGrouping<StackNode, GraphElement> item in from v in m_OriginalPos
                                                                        group v.Key by v.Value.stack)
                    {
                        if (item.Key != null && m_GraphView.elementsRemovedFromStackNode != null)
                        {
                            m_GraphView.elementsRemovedFromStackNode(item.Key, item);
                        }

                        foreach (GraphElement item2 in item)
                        {
                            item2.UpdatePresenterPosition();
                        }

                        m_MovedElements.AddRange(item);
                    }

                    if (base.target is GraphView graphView && graphView.graphViewChanged != null)
                    {
                        KeyValuePair<GraphElement, OriginalPos> keyValuePair = m_OriginalPos.First();
                        m_GraphViewChange.moveDelta = keyValuePair.Key.GetPosition().position - keyValuePair.Value.pos.position;
                        graphView.graphViewChanged(m_GraphViewChange);
                    }

                    m_MovedElements.Clear();
                }

                m_PanSchedule.Pause();
                if (m_ItemPanDiff != Vector3.zero)
                {
                    Vector3 position = m_GraphView.contentViewContainer.transform.position;
                    Vector3 scale = m_GraphView.contentViewContainer.transform.scale;
                    m_GraphView.UpdateViewTransform(position, scale);
                }

                if (selection.Count > 0 && m_PrevDropTarget != null)
                {
                    if (m_PrevDropTarget.CanAcceptDrop(selection))
                    {
                        using DragPerformEvent evt2 = MouseEventBase<DragPerformEvent>.GetPooled(evt);
                        SendDragAndDropEvent(evt2, selection, m_PrevDropTarget, m_GraphView);
                    }
                    else
                    {
                        using DragExitedEvent evt3 = MouseEventBase<DragExitedEvent>.GetPooled(evt);
                        SendDragAndDropEvent(evt3, selection, m_PrevDropTarget, m_GraphView);
                    }
                }

                if (snapEnabled)
                {
                    //           m_Snapper.EndSnap(m_GraphView);
                }

                base.target.ReleaseMouse();
                evt.StopPropagation();
            }

            selectedElement = null;
            m_Active = false;
            m_PrevDropTarget = null;
            m_Dragging = false;
            m_PrevDropTarget = null;
        }

        private void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Escape || m_GraphView == null || !m_Active)
            {
                return;
            }

            Dictionary<Scope, List<GraphElement>> dictionary = new Dictionary<Scope, List<GraphElement>>();
            foreach (KeyValuePair<GraphElement, OriginalPos> originalPo in m_OriginalPos)
            {
                OriginalPos value = originalPo.Value;
                if (value.stack != null)
                {
                    value.stack.InsertElement(value.stackIndex, originalPo.Key);
                    continue;
                }

                if (value.scope != null)
                {
                    if (!dictionary.ContainsKey(value.scope))
                    {
                        dictionary[value.scope] = new List<GraphElement>();
                    }

                    dictionary[value.scope].Add(originalPo.Key);
                }

                originalPo.Key.SetPosition(value.pos);
            }

            foreach (KeyValuePair<Scope, List<GraphElement>> item in dictionary)
            {
                item.Key.AddElements(item.Value);
            }

            m_PanSchedule.Pause();
            if (m_ItemPanDiff != Vector3.zero)
            {
                Vector3 position = m_GraphView.contentViewContainer.transform.position;
                Vector3 scale = m_GraphView.contentViewContainer.transform.scale;
                m_GraphView.UpdateViewTransform(position, scale);
            }

            using (DragExitedEvent evt = EventBase<DragExitedEvent>.GetPooled())
            {
                List<ISelectable> selection = m_GraphView.selection;
                SendDragAndDropEvent(evt, selection, m_PrevDropTarget, m_GraphView);
            }

            base.target.ReleaseMouse();
            e.StopPropagation();
        }
    }
}