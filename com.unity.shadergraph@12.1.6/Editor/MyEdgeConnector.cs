// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using PlasticGui.WorkspaceWindow.Merge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Services.Description;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UnityEngine.XR;
using static UnityEditor.ShaderGraph.Drawing.StickyNote;

namespace UnityEditor.Experimental.GraphView
{

    public class MyEdgeConnector<TEdge> : EdgeConnector where TEdge : Edge, new()
    {
        readonly EdgeDragHelper m_EdgeDragHelper;
        Edge m_EdgeCandidate;
        private bool m_Active;
        Vector2 m_MouseDownPosition;

        protected GraphView m_GraphView;
        private static List<Port> m_PotentialPorts = new List<Port>();
        private static LinkedList<Port> m_LastHandledPorts = new LinkedList<Port>();
        Edge m_HandEdge = null;

        internal const float k_ConnectionDistanceTreshold = 10f;

        public MyEdgeConnector(IEdgeConnectorListener listener)
        {
            m_EdgeDragHelper = new EdgeDragHelper<TEdge>(listener);
            m_Active = false;
            activators.Add(new ManipulatorActivationFilter { button = MouseButton.LeftMouse });
        }

        public override EdgeDragHelper edgeDragHelper => m_EdgeDragHelper;

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<MouseDownEvent>(OnMouseDown);
            target.RegisterCallback<MouseMoveEvent>(OnMouseMove);
            target.RegisterCallback<MouseUpEvent>(OnMouseUp);
            target.RegisterCallback<KeyDownEvent>(OnKeyDown);
            target.RegisterCallback<MouseCaptureOutEvent>(OnCaptureOut);
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<MouseDownEvent>(OnMouseDown);
            target.UnregisterCallback<MouseMoveEvent>(OnMouseMove);
            target.UnregisterCallback<MouseUpEvent>(OnMouseUp);
            target.UnregisterCallback<KeyDownEvent>(OnKeyDown);
        }

        protected virtual void OnMouseDown(MouseDownEvent e)
        {
            if (m_Active)
            {
                e.StopImmediatePropagation();
                return;
            }

            if (!CanStartManipulation(e))
            {
                return;
            }

            var graphElement = target as Port;
            if (graphElement == null)
            {
                return;
            }

            m_GraphView = graphElement.GetFirstAncestorOfType<GraphView>();
            m_LastHandledPorts.Clear();

            m_MouseDownPosition = e.localMousePosition;

            m_EdgeCandidate = new TEdge();
            m_EdgeDragHelper.draggedPort = graphElement;
            m_EdgeDragHelper.edgeCandidate = m_EdgeCandidate;

            if (m_EdgeDragHelper.HandleMouseDown(e))
            {
                m_Active = true;
                target.CaptureMouse();

                m_PotentialPorts = m_EdgeDragHelper.GetType()
                    .GetField("m_CompatiblePorts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .GetValue(m_EdgeDragHelper) as List<Port>;

                e.StopPropagation();
            }
            else
            {
                m_EdgeDragHelper.Reset();
                m_EdgeCandidate = null;
            }
        }

        protected bool m_ShowHand = false;
        float responseDist = 200;
        float initResponseDist = 40;
        float handTogehterDist = 160;
        float withdrawDistRate = 0.6f;
        Image handOpenImg = null;
        Image handHoldImg = null;
        Image currentHandImg = null;
        float holdedDist = Int32.MaxValue;
        public bool UpdateHandEdge(Vector2 mousePos)
        {
            bool holdChanged = false;
            var graphElement = target as Port;
            if (m_GraphView == null)
            {
                m_GraphView = graphElement.GetFirstAncestorOfType<GraphView>();
            }

            if (m_HandEdge == null)
            {
                m_HandEdge = new Edge();
                m_GraphView.AddElement(m_HandEdge);
                m_HandEdge.layer = Int32.MaxValue;
            }

            if (graphElement.GetSlot().isOutputSlot)
            {
                m_HandEdge.output = graphElement;
                m_HandEdge.input = null;
            } else
            {
                m_HandEdge.output = null;
                m_HandEdge.input = graphElement;
            }

            var center = graphElement.GetGlobalCenter();
            var dir = (Vector2)center - mousePos;
            var dist = dir.magnitude / m_GraphView.scale;
            dir.Normalize();

            m_ShowHand = dist < responseDist;
            m_HandEdge.visible = m_ShowHand;

            if (m_ShowHand)
            {
                float rate = (handTogehterDist - initResponseDist) 
                    / (responseDist - handTogehterDist);
                float delta = responseDist - dist; //>= 0

                float extra = responseDist / dist;
                float handDist = initResponseDist + delta * rate * extra * extra;

                delta = Math.Max( dist - handDist, 0);
                float awayDist = dist - holdedDist;
                bool hold = delta <= 0;
                if (hold)
                {
                    if (!m_LastHandledPorts.Contains(graphElement))
                    {
                        m_LastHandledPorts.AddLast(graphElement);
                        holdChanged = true;
                    }
                    if (dist < holdedDist)
                    {
                        holdedDist = dist;
                    }
                } else if (m_LastHandledPorts.Contains(graphElement))
                {
                    m_LastHandledPorts.Remove(graphElement);
                    holdedDist = Int32.MaxValue;

                    m_HandEdge.GetType()
                        .GetField("m_EdgeWidth",
                        System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance)
                        .SetValue(m_HandEdge, 2);
                    holdChanged = true;
                }

                if (!holdChanged && m_LastHandledPorts.Count > 1
                    && m_LastHandledPorts.Last() == graphElement
                    && awayDist > 10)
                {
                    m_LastHandledPorts.Remove(graphElement);
                    m_LastHandledPorts.AddBefore(m_LastHandledPorts.Last, graphElement);
                    holdedDist = Int32.MaxValue;
                    hold = false;

                    var newHold = m_LastHandledPorts.Last().edgeConnector as MyEdgeConnector<Edge>;
                    newHold.SetHandImage(true);
                }


                m_HandEdge.candidatePosition = mousePos + dir * delta * m_GraphView.scale;
                m_HandEdge.UpdateEdgeControl();
                var points = m_HandEdge.edgeControl.controlPoints;
                points[1] = points[0];
                //              points[2] = points[3];

                SetHandImage(hold);

                var deg = -Vector2.SignedAngle(points[2] - points[0], Vector2.up);
                currentHandImg.style.rotate = new StyleRotate(new Rotate(new Angle(deg, AngleUnit.Degree)));
            }
            return holdChanged;
        }

        public void SetHandImage(bool hold)
        {
            var graphElement = target as Port;
            if (!hold && handOpenImg == null)
            {
                Texture icon = Resources.Load<Texture2D>("Icons/hand_open") as Texture;
                handOpenImg = new Image { name = "handopen", image = icon };
                handOpenImg.style.scale = new StyleScale(new Scale(new Vector3(10, 10, 10)));
            }
            if (hold && handHoldImg == null)
            {
                Texture icon = Resources.Load<Texture2D>("Icons/hand_hold") as Texture;
                handHoldImg = new Image { name = "handhold", image = icon };
                handHoldImg.style.scale = new StyleScale(new Scale(new Vector3(10, 10, 10)));
            }

            var img = handOpenImg;
            if (m_LastHandledPorts.Count > 0 && graphElement == m_LastHandledPorts.Last())
            {
                img = handHoldImg;
            }

            if (img != currentHandImg)
            {
                var fromCap = m_HandEdge.edgeControl.GetType()
                    .GetField("m_FromCap"
                    , System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
                    .GetValue(m_HandEdge.edgeControl) as VisualElement;
                if (currentHandImg != null)
                {
                    fromCap.Remove(currentHandImg);
                }
                fromCap.Add(img);
                currentHandImg = img;
            }

        }

        public void WithdrawHand()
        {
            if (m_LastHandledPorts.Count == 0)
                return;
            var last = m_LastHandledPorts.Last();
            var graphElement = target as Port;
            if (graphElement != last)
            {
                var points = m_HandEdge.edgeControl.controlPoints;
                var dir = points[0] - points[2];
                var len = dir.magnitude;
                dir.Normalize();
                var p = points[2] + dir * len * withdrawDistRate;
                m_HandEdge.candidatePosition = m_HandEdge.LocalToWorld( p );

                m_HandEdge.UpdateEdgeControl();
                points = m_HandEdge.edgeControl.controlPoints;
                points[1] = points[0];

                m_HandEdge.GetType()
                    .GetField("m_EdgeWidth",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
                    .SetValue(m_HandEdge, 2);
            } else
            {
                m_HandEdge.GetType()
                    .GetField("m_EdgeWidth",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
                    .SetValue(m_HandEdge, 4);
            }
        }

        public void HideHandEdge()
        {
            if (m_HandEdge == null) return;

            var graphElement = target as Port;
            if (m_GraphView == null)
            {
                m_GraphView = graphElement.GetFirstAncestorOfType<GraphView>();
            }

            if (currentHandImg != null)
            {
                var fromCap = m_HandEdge.edgeControl.GetType()
                    .GetField("m_FromCap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .GetValue(m_HandEdge.edgeControl) as VisualElement;
                fromCap.Remove(currentHandImg);
                currentHandImg = null;
            }

            m_GraphView.RemoveElement(m_HandEdge);
            m_HandEdge = null; 
        }

        void OnCaptureOut(MouseCaptureOutEvent e)
        {
            m_Active = false;
            if (m_EdgeCandidate != null)
                Abort();
        }

        protected virtual void OnMouseMove(MouseMoveEvent e)
        {
            if (!m_Active) return;

            var graphElement = target as Port;
            bool holdChanged = false;
            foreach (var port in m_PotentialPorts)
            {
                if (port.connected) continue;

                var connector = port.edgeConnector as MyEdgeConnector<TEdge>;
                if (connector != null)
                {
                    holdChanged |= connector.UpdateHandEdge(e.mousePosition);
                }
            }
            if (holdChanged || m_LastHandledPorts.Count > 0)
            {
                foreach (var port in m_PotentialPorts)
                {
                    if (port.connected) continue;

                    var connector = port.edgeConnector as MyEdgeConnector<TEdge>;
                    if (connector != null && connector.m_ShowHand)
                    {
                        connector.WithdrawHand();
                    }
                }
            }

            m_EdgeDragHelper.HandleMouseMove(e);
            m_EdgeCandidate.candidatePosition = e.mousePosition;
            m_EdgeCandidate.UpdateEdgeControl();
            e.StopPropagation();
        }

        protected virtual void OnMouseUp(MouseUpEvent e)
        {
            if (!m_Active || !CanStopManipulation(e))
                return;

            if (CanPerformConnection(e.localMousePosition))
            {
                if (m_LastHandledPorts.Count > 0)
                {
                    var last = m_LastHandledPorts.Last();
                    var pos = last.worldBound.position;
                    //e.mousePosition = pos;
                    e.GetType().GetProperty("mousePosition")
                        .SetMethod.Invoke(e, new object[] { pos });
                }
                m_EdgeDragHelper.HandleMouseUp(e);
            }
            else
            {
                Abort();
            }

            foreach (var port in m_PotentialPorts)
            {
                var connector = port.edgeConnector as MyEdgeConnector<TEdge>;
                if (connector != null)
                {
                    connector.HideHandEdge();
                }
            }
            m_Active = false;
            m_EdgeCandidate = null;
            target.ReleaseMouse();
            e.StopPropagation();

            m_LastHandledPorts.Clear();
        }

        private void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode != KeyCode.Escape || !m_Active)
                return;

            Abort();

            m_Active = false;
            target.ReleaseMouse();
            e.StopPropagation();
        }

        protected virtual void Abort()
        {
            var graphView = target?.GetFirstAncestorOfType<GraphView>();
            graphView?.RemoveElement(m_EdgeCandidate);

            m_EdgeCandidate.input = null;
            m_EdgeCandidate.output = null;
            m_EdgeCandidate = null;

            m_EdgeDragHelper.Reset();
        }

        bool CanPerformConnection(Vector2 mousePosition)
        {
            return Vector2.Distance(m_MouseDownPosition, mousePosition) > k_ConnectionDistanceTreshold;
        }
    }
}