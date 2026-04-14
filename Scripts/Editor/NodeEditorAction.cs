using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using XNode;
using XNodeEditor.Internal;
#if UNITY_2019_1_OR_NEWER && USE_ADVANCED_GENERIC_MENU
using GenericMenu = XNodeEditor.AdvancedGenericMenu;
#endif

namespace XNodeEditor
{
    public partial class NodeEditorWindow {
        public enum NodeActivity { Idle, HoldNode, DragNode, HoldGrid, DragGrid }
        public static NodeActivity currentActivity = NodeActivity.Idle;
        public static bool isPanning { get; private set; }
        public static Vector2[] dragOffset;

        public static XNode.Node[] copyBuffer = null;

        public bool IsDraggingPort { get { return draggedOutput != null; } }
        public bool IsHoveringPort { get { return hoveredPort != null; } }
        public bool IsHoveringNode { get { return hoveredNode != null; } }
        public bool IsHoveringReroute { get { return hoveredReroute.port != null; } }

        public bool IsHoveringToolbar;

        /// <summary> Return the dragged port or null if not exist </summary>
        public XNode.NodePort DraggedOutputPort { get { XNode.NodePort result = draggedOutput; return result; } }
        /// <summary> Return the Hovered port or null if not exist </summary>
        public XNode.NodePort HoveredPort { get { XNode.NodePort result = hoveredPort; return result; } }
        /// <summary> Return the Hovered node or null if not exist </summary>
        public XNode.Node HoveredNode { get { XNode.Node result = hoveredNode; return result; } }

        [NonSerialized] private XNode.Node hoveredNode = null;
        [NonSerialized] public XNode.NodePort hoveredPort = null;
        [NonSerialized] private XNode.NodePort draggedOutput = null;
        [NonSerialized] private XNode.NodePort draggedOutputTarget = null;
        [NonSerialized] private XNode.NodePort autoConnectOutput = null;
        [NonSerialized] private List<Vector2> draggedOutputReroutes = new List<Vector2>();
        [NonSerialized] private Dictionary<Connection, Vector2> intersectedConnections = new();

        private RerouteReference hoveredReroute = new RerouteReference();
        public List<RerouteReference> selectedReroutes = new List<RerouteReference>();
        private Vector2 dragBoxStart;
        private UnityEngine.Object[] preBoxSelection; 
        private RerouteReference[] preBoxSelectionReroute;

        public Rect SelectionBox { get { return _selectionBox; } private set { _selectionBox = value; } }
        private Rect _selectionBox;

        private bool isDoubleClick = false;
        private Vector2 lastMousePosition;
        private float dragThreshold = .01f;
        private int frameControlID = 0;
        private int graphDragControlId = 0;
        private bool altHeld = false;


        public void Controls() {
            wantsMouseMove = true;
            Event e = Event.current;

            IsHoveringTopToolbar();

            switch (e.type) {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
                    if (e.type == EventType.DragPerform) {
                        DragAndDrop.AcceptDrag();
                        graphEditor.OnDropObjects(DragAndDrop.objectReferences);
                    }
                    break;
                case EventType.MouseMove:
                    //Keyboard commands will not get correct mouse position from Event
                    lastMousePosition = e.mousePosition;
                    break;
                case EventType.ScrollWheel:
                    float oldZoom = zoom;
                    if (e.delta.y > 0) zoom += 0.1f * zoom;
                    else zoom -= 0.1f * zoom;
                    if (NodeEditorPreferences.GetSettings().zoomToMouse)
                    {
                        panOffset += (1 - oldZoom / zoom) * (WindowToGridPosition(e.mousePosition) + panOffset);
                    }
                    break;
                case EventType.MouseDrag:
                    if (e.button == 0) {
                        if (IsDraggingPort) {
                            // Set target even if we can't connect, so as to prevent auto-conn menu from opening erroneously
                            if (IsHoveringPort && hoveredPort.IsInput && !draggedOutput.IsConnectedTo(hoveredPort)) {
                                draggedOutputTarget = hoveredPort;
                            } else {
                                draggedOutputTarget = null;
                            }
                            Repaint();
                        } else if (currentActivity == NodeActivity.HoldNode) {
                            RecalculateDragOffsets(e);
                            currentActivity = NodeActivity.DragNode;
                            Repaint();
                        }
                        if (currentActivity == NodeActivity.DragNode) {
                            // Holding ctrl inverts grid snap
                            bool gridSnap = NodeEditorPreferences.GetSettings().gridSnap;
                            if (e.control) gridSnap = !gridSnap;
                            XNode.Node selectedNode = null;

                            Vector2 mousePos = WindowToGridPosition(e.mousePosition);
                            // Move selected nodes with offset
                            for (int i = 0; i < Selection.objects.Length; i++) {
                                if (Selection.objects[i] is XNode.Node) {
                                    XNode.Node node = Selection.objects[i] as XNode.Node;
                                    if (selectedNode == null) selectedNode = node;

                                    Undo.RecordObject(node, "Moved Node");
                                    Vector2 initial = node.position;
                                    node.position = mousePos + dragOffset[i];
                                    if (gridSnap) {
                                        node.position.x = (Mathf.Round((node.position.x + 8) / 16) * 16) - 8;
                                        node.position.y = (Mathf.Round((node.position.y + 8) / 16) * 16) - 8;
                                    }

                                    // Offset portConnectionPoints instantly if a node is dragged so they aren't delayed by a frame.
                                    Vector2 offset = node.position - initial;
                                    if (offset.sqrMagnitude > 0) {
                                        foreach (XNode.NodePort output in node.Outputs) {
                                            Rect rect;
                                            if (portConnectionPoints.TryGetValue(output, out rect)) {
                                                rect.position += offset;
                                                portConnectionPoints[output] = rect;
                                            }
                                        }

                                        foreach (XNode.NodePort input in node.Inputs) {
                                            Rect rect;
                                            if (portConnectionPoints.TryGetValue(input, out rect)) {
                                                rect.position += offset;
                                                portConnectionPoints[input] = rect;
                                            }
                                        }
                                    }
                                }
                            }
                            // Move selected reroutes with offset
                            for (int i = 0; i < selectedReroutes.Count; i++) {
                                Vector2 pos = mousePos + dragOffset[Selection.objects.Length + i];
                                if (gridSnap) {
                                    pos.x = (Mathf.Round(pos.x / 16) * 16);
                                    pos.y = (Mathf.Round(pos.y / 16) * 16);
                                }
                                selectedReroutes[i].SetPoint(pos);
                            }


                            if (altHeld)
                            {
                                var input = selectedNode.Inputs.FirstOrDefault();
                                var output = selectedNode.Outputs.FirstOrDefault();

                                var inputConn = input?.Connection;
                                var outputConn = output?.Connection;

                                if (inputConn != null && outputConn != null)
                                {
                                    inputConn.Connect(outputConn);
                                }
                                selectedNode.ClearConnections();
                            }

                            //
                            List<NodePort> inputs = selectedNode.Inputs.ToList();
                            if (Selection.objects.Length == 1 && inputs.Count > 0 && !inputs[0].IsConnected && !altHeld)
                            {
                                Vector2 nodePos = GridToWindowPosition(selectedNode.position);
                                Vector2 size = nodeSizes.TryGetValue(selectedNode, out var s) ? s : new Vector2(50, 50);
                                 
                                Rect nodeRect = new Rect(nodePos, size / zoom);
                                bool isHoveringConnection = false;
                                intersectedConnections.Clear();
                                HashSet<XNode.NodePort> movingNodePorts = new HashSet<XNode.NodePort>(selectedNode.Ports);

                                foreach (var kvp in connectionCache)
                                {
                                    Connection connection = kvp.Key;
                                    List<Vector2> points = kvp.Value;

                                    if (movingNodePorts.Contains(connection.Input) || movingNodePorts.Contains(connection.Output))
                                        continue;

                                    Vector2 inputNodePos = GridToWindowPosition(connection.Input.node.position);
                                    Vector2 outputNodePos = GridToWindowPosition(connection.Output.node.position);

                                    if (!IsInsideBounds(inputNodePos, outputNodePos, nodeRect))
                                    {
                                        continue;
                                    }

                                    for (int i = 0; i < points.Count - 1; i++)
                                    {

                                        if (LineIntersectsRect(points[i], points[i + 1], nodeRect, out Vector2 intersection))
                                        {
                                            hoveredConnection = connection;
                                            isHoveringConnection = true;
                                            intersectedConnections[connection] = WindowToGridPosition(intersection);
                                            break;
                                        }
                                    }
                                }
                                if (!isHoveringConnection)
                                {
                                    hoveredConnection = null;
                                }

                                if (intersectedConnections.Count > 1)
                                {
                                    float dist = float.MaxValue;
                                    foreach (var kv in intersectedConnections)
                                    {
                                        float nDist = Vector2.SqrMagnitude(mousePos - kv.Value);
                                        if (nDist < dist)
                                        {
                                            dist = nDist;
                                            hoveredConnection = kv.Key;
                                        }
                                    }
                                }
                            }

                            //

                            NodeEditorWindow.current.wantsMouseEnterLeaveWindow = true;
                            int controlID = GUIUtility.GetControlID(FocusType.Passive);
                            GUIUtility.hotControl = controlID;

                            Repaint();
                        } else if (currentActivity == NodeActivity.HoldGrid) {
                            currentActivity = NodeActivity.DragGrid;
                            preBoxSelection = Selection.objects;
                            preBoxSelectionReroute = selectedReroutes.ToArray();
                            dragBoxStart = WindowToGridPosition(e.mousePosition);
                            Repaint();
                        } else if (currentActivity == NodeActivity.DragGrid) {
                            Vector2 boxStartPos = GridToWindowPosition(dragBoxStart);
                            Vector2 boxSize = e.mousePosition - boxStartPos;
                            if (boxSize.x < 0) { boxStartPos.x += boxSize.x; boxSize.x = Mathf.Abs(boxSize.x); }
                            if (boxSize.y < 0) { boxStartPos.y += boxSize.y; boxSize.y = Mathf.Abs(boxSize.y); }
                            SelectionBox = new Rect(boxStartPos, boxSize);
                            Repaint();
                        }

                        if (!IsHoveringNode && frameControlID == 0)
                        {
                            NodeEditorWindow.current.wantsMouseEnterLeaveWindow = true;
                            int controlID = GUIUtility.GetControlID(FocusType.Passive);
                            GUIUtility.hotControl = controlID;
                        }
                    } else if (e.button == 1 || e.button == 2) {
                        //check drag threshold for larger screens
                        panOffset += e.delta * zoom;
                        isPanning = true;
                        NodeEditorWindow.current.wantsMouseEnterLeaveWindow = true;
                        int controlID = GUIUtility.GetControlID(FocusType.Passive);
                        GUIUtility.hotControl = controlID;
                    }
                    break;
                case EventType.MouseDown:
                    Repaint();
                    if (e.button == 0) {
                        draggedOutputReroutes.Clear();

                        if (IsHoveringPort) {
                            if (hoveredPort.IsOutput) {
                                draggedOutput = hoveredPort;
                                autoConnectOutput = hoveredPort;
                            } else {
                                hoveredPort.VerifyConnections();
                                autoConnectOutput = null;
                                if (hoveredPort.IsConnected) {
                                    XNode.Node node = hoveredPort.node;
                                    XNode.NodePort output = hoveredPort.Connection;
                                    int outputConnectionIndex = output.GetConnectionIndex(hoveredPort);
                                    draggedOutputReroutes = output.GetReroutePoints(outputConnectionIndex);
                                    hoveredPort.Disconnect(output);
                                    draggedOutput = output;
                                    draggedOutputTarget = hoveredPort;
                                    if (NodeEditor.onUpdateNode != null) NodeEditor.onUpdateNode(node);
                                }
                            }
                        } else if (IsHoveringNode && IsHoveringTitle(hoveredNode) && !IsHoveringToolbar) {
                            graphDragControlId = GUIUtility.GetControlID(FocusType.Passive);

                            // If mousedown on node header, select or deselect
                            if (!Selection.Contains(hoveredNode)) {
                                SelectNode(hoveredNode, e.control || e.shift);
                                if (!e.control && !e.shift) selectedReroutes.Clear();
                            } else if (e.control || e.shift) DeselectNode(hoveredNode);

                            // Cache double click state, but only act on it in MouseUp - Except ClickCount only works in mouseDown.
                            isDoubleClick = (e.clickCount == 2);

                            //e.Use();
                            currentActivity = NodeActivity.HoldNode;
                        } else if (IsHoveringReroute) {
                            // If reroute isn't selected
                            if (!selectedReroutes.Contains(hoveredReroute)) {
                                // Add it
                                if (e.control || e.shift) selectedReroutes.Add(hoveredReroute);
                                // Select it
                                else {
                                    selectedReroutes = new List<RerouteReference>() { hoveredReroute };
                                    Selection.activeObject = null;
                                }

                            }
                            // Deselect
                            else if (e.control || e.shift) selectedReroutes.Remove(hoveredReroute);
                            e.Use();
                            currentActivity = NodeActivity.HoldNode;
                        }
                        // If mousedown on grid background, deselect all
                        else if (!IsHoveringNode) {
                            currentActivity = NodeActivity.HoldGrid;
                            if (!e.control && !e.shift) {
                                selectedReroutes.Clear();
                                Selection.activeObject = null;
                            }
                        }
                        else if (IsHoveringNode && !IsHoveringTitle(hoveredNode))
                        {
                            frameControlID = 1;
                        }
                    }
                    break;
                case EventType.MouseUp:
                    if (currentActivity != NodeActivity.Idle || IsDraggingPort || isPanning)
                    {
                        if (GUIUtility.hotControl == graphDragControlId)
                        {
                            GUIUtility.hotControl = 0;
                            NodeEditorWindow.current.wantsMouseEnterLeaveWindow = false;
                        }
                    }
                    frameControlID = 0;
                    if (e.button == 0) {
                        //Port drag release
                        if (IsDraggingPort) {
                            // If connection is valid, save it
                            if (draggedOutputTarget != null && graphEditor.CanConnect(draggedOutput, draggedOutputTarget) && !WouldCreateCycle(draggedOutput.node, draggedOutputTarget.node)) {
                                XNode.Node node = draggedOutputTarget.node;
                                if (graph.nodes.Count != 0) draggedOutput.Connect(draggedOutputTarget);

                                // ConnectionIndex can be -1 if the connection is removed instantly after creation
                                int connectionIndex = draggedOutput.GetConnectionIndex(draggedOutputTarget);
                                if (connectionIndex != -1) {
                                    draggedOutput.GetReroutePoints(connectionIndex).AddRange(draggedOutputReroutes);
                                    if (NodeEditor.onUpdateNode != null) NodeEditor.onUpdateNode(node);
                                    EditorUtility.SetDirty(graph);
                                }
                            }
                            // Open context menu for auto-connection if there is no target node
                            else if (draggedOutputTarget == null && NodeEditorPreferences.GetSettings().dragToCreate && autoConnectOutput != null) {
                                GenericMenu menu = new GenericMenu();
                                graphEditor.AddContextMenuItems(menu, draggedOutput.ValueType);
                                menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
                            }
                            //Release dragged connection
                            draggedOutput = null;
                            draggedOutputTarget = null;
                            EditorUtility.SetDirty(graph);
                            if (NodeEditorPreferences.GetSettings().autoSave) AssetDatabase.SaveAssets();
                        } else if (currentActivity == NodeActivity.DragNode) {

                            IEnumerable<XNode.Node> nodes = Selection.objects.Where(x => x is XNode.Node).Select(x => x as XNode.Node);

                            //
                            if (hoveredConnection != null && !altHeld)
                            {
                                var node = nodes.FirstOrDefault();
                                var selectedInput = node.Inputs.FirstOrDefault();
                                var selectedOutput = node.Outputs.FirstOrDefault();
                                if (selectedInput != null && !selectedInput.IsConnected)
                                {
                                    selectedInput.Connect(hoveredConnection.Output);
                                    selectedOutput.Connect(hoveredConnection.Input);
                                }
                                hoveredConnection = null;
                            }
                            //


                            foreach (XNode.Node node in nodes) EditorUtility.SetDirty(node);
                            if (NodeEditorPreferences.GetSettings().autoSave) AssetDatabase.SaveAssets();
                        } else if (!IsHoveringNode) {
                            // If click outside node, release field focus
                            if (!isPanning) {
                                EditorGUI.FocusTextInControl(null);
                                EditorGUIUtility.editingTextField = false;
                            }
                            if (NodeEditorPreferences.GetSettings().autoSave) AssetDatabase.SaveAssets();
                        }

                        // If click node header, select it.
                        if (currentActivity == NodeActivity.HoldNode && !(e.control || e.shift)) {
                            selectedReroutes.Clear();
                            SelectNode(hoveredNode, false);

                            // Double click to center node
                            if (isDoubleClick) {
                                Vector2 nodeDimension = nodeSizes.ContainsKey(hoveredNode) ? nodeSizes[hoveredNode] / 2 : Vector2.zero;
                                panOffset = -hoveredNode.position - nodeDimension;
                            }
                        }

                        // If click reroute, select it.
                        if (IsHoveringReroute && !(e.control || e.shift)) {
                            selectedReroutes = new List<RerouteReference>() { hoveredReroute };
                            Selection.activeObject = null;
                        }

                        Repaint();
                        currentActivity = NodeActivity.Idle;
                    } else if (e.button == 1) {
                        if (!isPanning) {
                            if (IsDraggingPort) {
                                draggedOutputReroutes.Add(WindowToGridPosition(e.mousePosition));
                            } else if (currentActivity == NodeActivity.DragNode && Selection.activeObject == null && selectedReroutes.Count == 1) {
                                selectedReroutes[0].InsertPoint(selectedReroutes[0].GetPoint());
                                selectedReroutes[0] = new RerouteReference(selectedReroutes[0].port, selectedReroutes[0].connectionIndex, selectedReroutes[0].pointIndex + 1);
                            } else if (IsHoveringReroute) {
                                ShowRerouteContextMenu(hoveredReroute);
                            } else if (IsHoveringPort) {
                                ShowPortContextMenu(hoveredPort);
                            } else if (IsHoveringNode && IsHoveringTitle(hoveredNode)) {
                                if (!Selection.Contains(hoveredNode)) SelectNode(hoveredNode, false);
                                autoConnectOutput = null;
                                GenericMenu menu = new GenericMenu();
                                NodeEditor.GetEditor(hoveredNode, this).AddContextMenuItems(menu);
                                menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
                                e.Use(); // Fixes copy/paste context menu appearing in Unity 5.6.6f2 - doesn't occur in 2018.3.2f1 Probably needs to be used in other places.
                            } else if (!IsHoveringNode) {
                                autoConnectOutput = null;
                                GenericMenu menu = new GenericMenu();
                                graphEditor.AddContextMenuItems(menu);
                                menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero));
                            }
                        }
                        isPanning = false;
                    } else if (e.button == 2)
                    {
                        isPanning = false;
                    }
                    // Reset DoubleClick
                    isDoubleClick = false;
                    break;
                case EventType.KeyDown:
                    if (EditorGUIUtility.editingTextField || GUIUtility.keyboardControl != 0) break;
                    else if (e.keyCode == KeyCode.F) Home();
                    if (NodeEditorUtilities.IsMac()) {
                        if (e.keyCode == KeyCode.Return) RenameSelectedNode();
                    } else {
                        if (e.keyCode == KeyCode.F2) RenameSelectedNode();
                    }
                    if (e.keyCode == KeyCode.A) {
                        if (Selection.objects.Any(x => graph.nodes.Contains(x as XNode.Node))) {
                            foreach (XNode.Node node in graph.nodes) {
                                DeselectNode(node);
                            }
                        } else {
                            foreach (XNode.Node node in graph.nodes) {
                                SelectNode(node, true);
                            }
                        }
                        Repaint();
                    }
                    if (e.keyCode == KeyCode.X)
                    {
                        RemoveSelectedNodes();
                        e.Use();
                    }
                    if (e.keyCode == KeyCode.LeftAlt || e.keyCode == KeyCode.RightAlt)
                    {
                        altHeld = true;
                        hoveredConnection = null;
                    }
                    break;
                case EventType.KeyUp:
                    if (e.keyCode == KeyCode.LeftAlt || e.keyCode == KeyCode.RightAlt)
                    {
                        altHeld = false;
                    }
                    break;
                case EventType.ValidateCommand:
                case EventType.ExecuteCommand:
                    if (e.commandName == "SoftDelete") {
                        if (e.type == EventType.ExecuteCommand) RemoveSelectedNodes();
                        e.Use();
                    } else if (NodeEditorUtilities.IsMac() && e.commandName == "Delete") {
                        if (e.type == EventType.ExecuteCommand) RemoveSelectedNodes();
                        e.Use();
                    } else if (e.commandName == "Duplicate") {
                        if (e.type == EventType.ExecuteCommand) DuplicateSelectedNodes();
                        e.Use();
                    } else if (e.commandName == "Copy") {
                        if (true) {
                            if (e.type == EventType.ExecuteCommand) CopySelectedNodes();
                            e.Use();
                        }
                    } else if (e.commandName == "Paste") {
                        if (true)
                        {
                            if (e.type == EventType.ExecuteCommand) PasteNodes(WindowToGridPosition(lastMousePosition));
                            e.Use();
                        }
                    }
                    Repaint();
                    break;
                case EventType.Ignore:
                    // If release mouse outside window
                    if (e.rawType == EventType.MouseUp && currentActivity == NodeActivity.DragGrid) {
                        Repaint();
                        currentActivity = NodeActivity.Idle;
                    }
                    break;
            }
            if (!e.alt)
            {
                altHeld = false;
            }
        }

        private void RecalculateDragOffsets(Event current) {
            dragOffset = new Vector2[Selection.objects.Length + selectedReroutes.Count];
            // Selected nodes
            for (int i = 0; i < Selection.objects.Length; i++) {
                if (Selection.objects[i] is XNode.Node) {
                    XNode.Node node = Selection.objects[i] as XNode.Node;
                    dragOffset[i] = node.position - WindowToGridPosition(current.mousePosition);
                }
            }

            // Selected reroutes
            for (int i = 0; i < selectedReroutes.Count; i++) {
                dragOffset[Selection.objects.Length + i] = selectedReroutes[i].GetPoint() - WindowToGridPosition(current.mousePosition);
            }
        }

        /// <summary> Puts all selected nodes in focus. If no nodes are present, resets view and zoom to to origin </summary>
        public void Home() {
            var nodes = Selection.objects.Where(o => o is XNode.Node).Cast<XNode.Node>().ToList();
            if (nodes.Count > 0) {
                Vector2 minPos = nodes.Select(x => x.position).Aggregate((x, y) => new Vector2(Mathf.Min(x.x, y.x), Mathf.Min(x.y, y.y)));
                Vector2 maxPos = nodes.Select(x => x.position + (nodeSizes.ContainsKey(x) ? nodeSizes[x] : Vector2.zero)).Aggregate((x, y) => new Vector2(Mathf.Max(x.x, y.x), Mathf.Max(x.y, y.y)));
                panOffset = -(minPos + (maxPos - minPos) / 2f);
            } else {
                zoom = 2;
                panOffset = Vector2.zero;
            }
        }

        /// <summary> Remove nodes in the graph in Selection.objects</summary>
        public void RemoveSelectedNodes() {
            // We need to delete reroutes starting at the highest point index to avoid shifting indices
            selectedReroutes = selectedReroutes.OrderByDescending(x => x.pointIndex).ToList();
            for (int i = 0; i < selectedReroutes.Count; i++) {
                selectedReroutes[i].RemovePoint();
            }
            selectedReroutes.Clear();
            foreach (UnityEngine.Object item in Selection.objects) {
                if (item is XNode.Node) {
                    XNode.Node node = item as XNode.Node;
                    graphEditor.RemoveNode(node);
                }
            }
        }

        /// <summary> Initiate a rename on the currently selected node </summary>
        public void RenameSelectedNode() {
            if (Selection.objects.Length == 1 && Selection.activeObject is XNode.Node) {
                XNode.Node node = Selection.activeObject as XNode.Node;
                Vector2 size;
                if (nodeSizes.TryGetValue(node, out size)) {
                    RenamePopup.Show(Selection.activeObject, size.x);
                } else {
                    RenamePopup.Show(Selection.activeObject);
                }
            }
        }

        /// <summary> Draw this node on top of other nodes by placing it last in the graph.nodes list </summary>
        public void MoveNodeToTop(XNode.Node node) {
            int index;
            while ((index = graph.nodes.IndexOf(node)) != graph.nodes.Count - 1) {
                graph.nodes[index] = graph.nodes[index + 1];
                graph.nodes[index + 1] = node;
            }
        }

        /// <summary> Duplicate selected nodes and select the duplicates </summary>
        public void DuplicateSelectedNodes() {
            // Get selected nodes which are part of this graph
            XNode.Node[] selectedNodes = Selection.objects.Select(x => x as XNode.Node).Where(x => x != null && x.graph == graph).ToArray();
            if (selectedNodes == null || selectedNodes.Length == 0) return;
            // Get top left node position
            Vector2 topLeftNode = selectedNodes.Select(x => x.position).Aggregate((x, y) => new Vector2(Mathf.Min(x.x, y.x), Mathf.Min(x.y, y.y)));
            InsertDuplicateNodes(selectedNodes, topLeftNode + new Vector2(30, 30));
        }

        public void CopySelectedNodes() {
            copyBuffer = Selection.objects.Select(x => x as XNode.Node).Where(x => x != null && x.graph == graph).ToArray();
        }

        public List<XNode.Node> SelectedNodes()
        {
            return Selection.objects.Select(x => x as XNode.Node).Where(x => x != null && x.graph == graph).ToList();
        }

        public void PasteNodes(Vector2 pos) {
            InsertDuplicateNodes(copyBuffer, pos);
        }

        private void InsertDuplicateNodes(XNode.Node[] nodes, Vector2 topLeft) {
            if (nodes == null || nodes.Length == 0) return;

            // Get top-left node
            Vector2 topLeftNode = nodes.Select(x => x.position).Aggregate((x, y) => new Vector2(Mathf.Min(x.x, y.x), Mathf.Min(x.y, y.y)));
            Vector2 offset = topLeft - topLeftNode;

            UnityEngine.Object[] newNodes = new UnityEngine.Object[nodes.Length];
            Dictionary<XNode.Node, XNode.Node> substitutes = new Dictionary<XNode.Node, XNode.Node>();
            for (int i = 0; i < nodes.Length; i++) {
                XNode.Node srcNode = nodes[i];
                if (srcNode == null) continue;

                // Check if user is allowed to add more of given node type
                XNode.Node.DisallowMultipleNodesAttribute disallowAttrib;
                Type nodeType = srcNode.GetType();
                if (NodeEditorUtilities.GetAttrib(nodeType, out disallowAttrib)) {
                    int typeCount = graph.nodes.Count(x => x.GetType() == nodeType);
                    if (typeCount >= disallowAttrib.max) continue;
                }

                XNode.Node newNode = graphEditor.CopyNode(srcNode);
                substitutes.Add(srcNode, newNode);
                newNode.position = srcNode.position + offset;
                newNodes[i] = newNode;

                newNode.isPasted = true;
            }

            // Walk through the selected nodes again, recreate connections, using the new nodes
            for (int i = 0; i < nodes.Length; i++) {
                XNode.Node srcNode = nodes[i];
                if (srcNode == null) continue;
                foreach (XNode.NodePort port in srcNode.Ports) {
                    for (int c = 0; c < port.ConnectionCount; c++) {
                        XNode.NodePort inputPort = port.direction == XNode.NodePort.IO.Input ? port : port.GetConnection(c);
                        XNode.NodePort outputPort = port.direction == XNode.NodePort.IO.Output ? port : port.GetConnection(c);

                        XNode.Node newNodeIn, newNodeOut;
                        if (substitutes.TryGetValue(inputPort.node, out newNodeIn) && substitutes.TryGetValue(outputPort.node, out newNodeOut)) {
                            newNodeIn.UpdatePorts();
                            newNodeOut.UpdatePorts();
                            inputPort = newNodeIn.GetInputPort(inputPort.fieldName);
                            outputPort = newNodeOut.GetOutputPort(outputPort.fieldName);
                        }
                        if (!inputPort.IsConnectedTo(outputPort)) inputPort.Connect(outputPort);
                    }
                }
            }
            EditorUtility.SetDirty(graph);
            // Select the new nodes
            Selection.objects = newNodes;
        }

        /// <summary> Draw a connection as we are dragging it </summary>
        public void DrawDraggedConnection() {
            if (IsDraggingPort) {
                Gradient gradient = graphEditor.GetNoodleGradient(draggedOutput, null);
                float thickness = graphEditor.GetNoodleThickness(draggedOutput, null);
                NoodlePath path = graphEditor.GetNoodlePath(draggedOutput, null);
                NoodleStroke stroke = graphEditor.GetNoodleStroke(draggedOutput, null);

                Rect fromRect;
                if (!_portConnectionPoints.TryGetValue(draggedOutput, out fromRect)) return;
                List<Vector2> gridPoints = new List<Vector2>();
                gridPoints.Add(fromRect.center);
                for (int i = 0; i < draggedOutputReroutes.Count; i++) {
                    gridPoints.Add(draggedOutputReroutes[i]);
                }
                if (draggedOutputTarget != null) gridPoints.Add(portConnectionPoints[draggedOutputTarget].center);
                else gridPoints.Add(WindowToGridPosition(Event.current.mousePosition));

                DrawNoodle(gradient, path, stroke, thickness, gridPoints);

                GUIStyle portStyle = NodeEditorWindow.current.graphEditor.GetPortStyle(draggedOutput);
                Color bgcol = Color.black;
                Color frcol = gradient.colorKeys[0].color;
                bgcol.a = 0.6f;
                frcol.a = 0.6f;

                // Loop through reroute points again and draw the points
                for (int i = 0; i < draggedOutputReroutes.Count; i++) {
                    // Draw reroute point at position
                    Rect rect = new Rect(draggedOutputReroutes[i], new Vector2(16, 16));
                    rect.position = new Vector2(rect.position.x - 8, rect.position.y - 8);
                    rect = GridToWindowRect(rect);

                    NodeEditorGUILayout.DrawPortHandle(rect, bgcol, frcol, portStyle.normal.background, portStyle.active.background);
                }
            }
        }

        public void ClearSelectionBox()
        {
            SelectionBox = new();
        }

        bool IsOverControl(XNode.Node node)
        {
            Vector2 mousePos = Event.current.mousePosition;
            Vector2 nodePos = GridToWindowPosition(node.position);

            foreach (Rect r in node.controlRects)
            {
                Rect worldRect = new Rect(
                    nodePos + r.position / zoom,
                    r.size / zoom
                );

                if (worldRect.Contains(mousePos))
                    return true;
            }

            return false;
        }

        bool IsHoveringTitle(XNode.Node node) {
            Vector2 mousePos = Event.current.mousePosition;
            //Get node position
            Vector2 nodePos = GridToWindowPosition(node.position);
            float width;
            Vector2 size;
            if (nodeSizes.TryGetValue(node, out size)) width = size.x;
            else width = 200;
            //float height = size.y;
            Rect windowRect = new Rect(nodePos, new Vector2(width / zoom, 30 / zoom));

            return windowRect.Contains(mousePos);
        }

        bool IsHoveringTopToolbar()
        {
            Vector2 mousePos = Event.current.mousePosition;
            if (mousePos.y < 23)
            {
                IsHoveringToolbar = true;
                return true;
            }
            IsHoveringToolbar = false;
            return false;
        }

        private bool WouldCreateCycle(XNode.Node source, XNode.Node target)
        {
            return HasPath(target, source, new HashSet<XNode.Node>());
        }

        private bool HasPath(XNode.Node current, XNode.Node goal, HashSet<XNode.Node> visited)
        {
            if (current == goal)
                return true;

            if (!visited.Add(current))
                return false;

            foreach (var output in current.Outputs)
            {
                foreach (var connection in output.GetConnections())
                {
                    if (HasPath(connection.node, goal, visited))
                        return true;
                }
            }

            return false;
        }

        /// <summary> Attempt to connect dragged output to target node </summary>
        public void AutoConnect(XNode.Node node) {
            if (autoConnectOutput == null) return;

            // Find compatible input port
            XNode.NodePort inputPort = node.Ports.FirstOrDefault(x => x.IsInput && graphEditor.CanConnect(autoConnectOutput, x));
            if (inputPort != null) autoConnectOutput.Connect(inputPort);

            // Save changes
            EditorUtility.SetDirty(graph);
            if (NodeEditorPreferences.GetSettings().autoSave) AssetDatabase.SaveAssets();
            autoConnectOutput = null;
        }

        bool IsInsideBounds(Vector2 p1, Vector2 p2, Rect r)
        {
            float minX = Mathf.Min(p1.x, p2.x) - 200;
            float maxX = Mathf.Max(p1.x, p2.x) + 200;
            float minY = Mathf.Min(p1.y, p2.y) - 300;
            float maxY = Mathf.Max(p1.y, p2.y) + 300;

            Rect connectionArea = new Rect(minX, minY, maxX - minX, maxY - minY);

            if (!r.Overlaps(connectionArea))
            {
                return false;
            }

            return true;
        }

        bool LineIntersectsRect(Vector2 p1, Vector2 p2, Rect r, out Vector2 intersectionPoint)
        {
            intersectionPoint = Vector2.zero;

            if (r.Contains(p1))
            {
                intersectionPoint = p1;
                return true;
            }

            // 2. Early Out (AABB Check)
            float minX = Mathf.Min(p1.x, p2.x);
            float maxX = Mathf.Max(p1.x, p2.x);
            float minY = Mathf.Min(p1.y, p2.y);
            float maxY = Mathf.Max(p1.y, p2.y);

            if (maxX < r.xMin || minX > r.xMax || maxY < r.yMin || minY > r.yMax)
                return false;

            Vector2[] corners = {
                new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin), // Top
                new Vector2(r.xMax, r.yMin), new Vector2(r.xMax, r.yMax), // Right
                new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax), // Bottom
                new Vector2(r.xMin, r.yMax), new Vector2(r.xMin, r.yMin)  // Left
            };

            float minU = float.MaxValue;
            bool hit = false;

            for (int i = 0; i < 8; i += 2)
            {
                float u = GetIntersectionU(p1, p2, corners[i], corners[i + 1]);

                // If we found a valid intersection, keep the one closest to p1 (the entry point)
                if (u != -1f && u < minU)
                {
                    minU = u;
                    hit = true;
                }
            }

            if (hit)
            {
                intersectionPoint = p1 + minU * (p2 - p1);
            }
            else if (r.Contains(p2))
            {
                intersectionPoint = p2;
                return true;
            }

            return hit;
        }

        float GetIntersectionU(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            float d = (a2.x - a1.x) * (b2.y - b1.y) - (a2.y - a1.y) * (b2.x - b1.x);

            if (Mathf.Approximately(d, 0))
                return -1f;

            float u = ((b1.x - a1.x) * (b2.y - b1.y) - (b1.y - a1.y) * (b2.x - b1.x)) / d;
            float v = ((b1.x - a1.x) * (a2.y - a1.y) - (b1.y - a1.y) * (a2.x - a1.x)) / d;

            if (u >= 0f && u <= 1f && v >= 0f && v <= 1f)
                return u;

            return -1f;
        }
    }
}