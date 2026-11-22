using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ControlLibrary;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Watch_Face_Editor.McpBridge;

namespace Watch_Face_Editor
{
    // Optional MCP bridge. Kept in its own partial file so Form1.cs only gains one call.
    //  - off by default; turned on/off with the small "MCP" indicator next to the version number (top right);
    //  - when on, a local MCP server (same Windows user, named pipe) can read and edit the open project
    //    through the editor's own code: the JSON model, the preview renderer and the packer.
    //  - nothing here changes what the editor generates for the watch.
    public partial class Form1
    {
        private McpBridgeServer mcpBridgeServer;
        private Label label_McpBridge;
        private ToolTip toolTip_McpBridge;
        private int mcpBridgeBusy;         // 1 while a bridge command runs on the UI thread
        private int mcpBridgePreviewCount; // file name counter for rendered previews

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        /// <summary>Called at the end of Form1_Load.</summary>
        private void McpBridge_Init()
        {
            try { McpBridge_CreateIndicator(); }
            catch (Exception ex) { Logger.WriteLine("McpBridge_CreateIndicator error: " + ex); }
            FormClosed += (s, e) => McpBridge_Stop();
            try
            {
                if (ProgramSettings != null && ProgramSettings.McpBridgeEnabled) McpBridge_Start();
            }
            catch (Exception ex) { Logger.WriteLine("McpBridge_Start error: " + ex); }
            McpBridge_UpdateIndicator();
        }

        private void McpBridge_Start()
        {
            if (mcpBridgeServer != null) return;
            string pipeName = McpBridgeServer.PipePrefix + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            mcpBridgeServer = new McpBridgeServer(pipeName, McpBridge_HandleRequest);
            mcpBridgeServer.Start();
        }

        private void McpBridge_Stop()
        {
            if (mcpBridgeServer == null) return;
            mcpBridgeServer.Dispose();
            mcpBridgeServer = null;
        }

        // ---------------------------------------------------------------- indicator (top right)

        private void McpBridge_CreateIndicator()
        {
            Label anchor = label_version; // "v 18.3", top right
            if (anchor == null || anchor.Parent == null) return;

            label_McpBridge = new Label();
            label_McpBridge.Name = "label_McpBridge";
            // As tall as the dark mode toggle next to it. The version label uses a 5pt font, too small to click.
            label_McpBridge.AutoSize = false;
            label_McpBridge.Font = new Font(anchor.Font.FontFamily, Math.Max(8f, McpBridge_IndicatorMinimumSize().Height * 0.7f),
                                            FontStyle.Bold, GraphicsUnit.Pixel);
            label_McpBridge.TextAlign = ContentAlignment.MiddleCenter;
            label_McpBridge.BackColor = anchor.BackColor;
            label_McpBridge.Anchor = anchor.Anchor;
            label_McpBridge.Cursor = Cursors.Hand;
            label_McpBridge.Click += (s, e) => McpBridge_Toggle();
            toolTip_McpBridge = new ToolTip();
            anchor.Parent.Controls.Add(label_McpBridge);
            label_McpBridge.BringToFront();
            anchor.LocationChanged += (s, e) => McpBridge_PlaceIndicator();
            if (button_ThemeToggle != null) button_ThemeToggle.LocationChanged += (s, e) => McpBridge_PlaceIndicator();
        }

        /// <summary>The indicator is never smaller than the dark mode toggle (34 x 16 at 100%).</summary>
        private Size McpBridge_IndicatorMinimumSize()
        {
            if (button_ThemeToggle != null && button_ThemeToggle.Width > 0 && button_ThemeToggle.Height > 0) return button_ThemeToggle.Size;
            return new Size((int)Math.Round(34 * currentDPI), (int)Math.Round(16 * currentDPI));
        }

        private void McpBridge_PlaceIndicator()
        {
            if (label_McpBridge == null || label_version == null) return;
            // Sits to the left of the dark mode toggle (or of the version number when there is no toggle).
            Control neighbour = label_version;
            if (button_ThemeToggle != null && button_ThemeToggle.Parent == label_version.Parent) neighbour = button_ThemeToggle;
            int gap = (int)Math.Round(6 * currentDPI);
            int x = neighbour.Left - label_McpBridge.Width - gap;
            int y = label_version.Top + label_version.Height / 2 - label_McpBridge.Height / 2;
            label_McpBridge.Location = new Point(Math.Max(0, x), Math.Max(0, y));
        }

        private void McpBridge_UpdateIndicator()
        {
            if (label_McpBridge == null) return;
            bool on = mcpBridgeServer != null;
            label_McpBridge.Text = on ? "● MCP" : "○ MCP";
            label_McpBridge.ForeColor = on ? Color.FromArgb(0x2E, 0xA0, 0x5F) : Color.Gray;
            Size text = TextRenderer.MeasureText(label_McpBridge.Text, label_McpBridge.Font);
            Size minimum = McpBridge_IndicatorMinimumSize();
            label_McpBridge.Size = new Size(Math.Max(text.Width, minimum.Width), Math.Max(text.Height, minimum.Height));
            toolTip_McpBridge.SetToolTip(label_McpBridge, McpBridge_Text(on ? "tip_on" : "tip_off"));
            McpBridge_PlaceIndicator();
        }

        private void McpBridge_Toggle()
        {
            if (ProgramSettings == null) return;
            bool turnOn = mcpBridgeServer == null;
            if (turnOn)
            {
                if (MessageBox.Show(McpBridge_Text("confirm"), "MCP", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                try { McpBridge_Start(); }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "MCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else McpBridge_Stop();

            ProgramSettings.McpBridgeEnabled = mcpBridgeServer != null;
            Save_Settings();
            McpBridge_UpdateIndicator();
        }

        // Text set in code on purpose: no .resx is touched, so the translations stay intact.
        private string McpBridge_Text(string key)
        {
            bool pt = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";
            string pipe = mcpBridgeServer != null ? mcpBridgeServer.PipeName : "";
            switch (key)
            {
                case "tip_on":
                    return pt
                        ? "Ponte MCP ligada (" + pipe + ").\nUm servidor MCP local pode ler e editar o projeto aberto.\nClique para desligar."
                        : "MCP bridge is on (" + pipe + ").\nA local MCP server can read and edit the open project.\nClick to turn it off.";
                case "tip_off":
                    return pt
                        ? "Ponte MCP desligada. Clique para ligar."
                        : "MCP bridge is off. Click to turn it on.";
                case "confirm":
                    return pt
                        ? "Ligar a ponte MCP?\n\nProgramas da sua conta do Windows neste computador (por exemplo, um servidor MCP usado por um " +
                          "assistente de IA) poderão ler e alterar o projeto aberto neste editor, gerar prévias e empacotar o mostrador.\n\n" +
                          "A ponte não usa a rede e fica ligada até você desligar."
                        : "Turn on the MCP bridge?\n\nPrograms of your Windows account on this computer (for example an MCP server used by an " +
                          "AI assistant) will be able to read and change the project open in this editor, render previews and pack the watch face.\n\n" +
                          "The bridge does not use the network and stays on until you turn it off.";
            }
            return key;
        }

        // ---------------------------------------------------------------- request handling

        /// <summary>Runs on the pipe thread: parses one request line and returns one response line.</summary>
        private string McpBridge_HandleRequest(string requestLine)
        {
            JObject response = new JObject();
            JToken id = null;
            try
            {
                JObject request = JObject.Parse(requestLine);
                id = request["id"];
                string cmd = (string)request["cmd"];
                JObject args = request["args"] as JObject;
                if (args == null) args = new JObject();
                int timeoutMs = 30000;
                if (request["timeout_ms"] != null) timeoutMs = Math.Max(1000, Math.Min(600000, (int)request["timeout_ms"]));
                if (string.IsNullOrEmpty(cmd)) throw new McpBridgeException("bad_request", "Missing \"cmd\".");

                response["result"] = McpBridge_RunOnUiThread(cmd, args, timeoutMs);
                response["ok"] = true;
            }
            catch (McpBridgeException ex)
            {
                response = McpBridge_Error(ex.Code, ex.Message);
            }
            catch (JsonException ex)
            {
                response = McpBridge_Error("bad_request", ex.Message);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("MCP bridge error: " + ex);
                response = McpBridge_Error("internal_error", ex.GetType().Name + ": " + ex.Message);
            }
            if (id != null) response["id"] = id;
            return response.ToString(Formatting.None);
        }

        private static JObject McpBridge_Error(string code, string message)
        {
            JObject error = new JObject();
            error["code"] = code;
            error["message"] = message;
            JObject response = new JObject();
            response["ok"] = false;
            response["error"] = error;
            return response;
        }

        /// <summary>Everything that touches the form runs on the UI thread; the pipe thread only waits for it.</summary>
        private JToken McpBridge_RunOnUiThread(string cmd, JObject args, int timeoutMs)
        {
            if (IsDisposed || !IsHandleCreated) throw new McpBridgeException("editor_closing", "The editor is closing.");
            if (Interlocked.CompareExchange(ref mcpBridgeBusy, 1, 0) != 0)
                throw new McpBridgeException("busy", "The editor is still running the previous bridge command. " +
                    "If a dialog is open in the editor, answer it first.");

            TaskCompletionSource<JToken> done = new TaskCompletionSource<JToken>();
            try
            {
                BeginInvoke(new Action(async () =>
                {
                    try { done.TrySetResult(await McpBridge_Execute(cmd, args)); }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { Interlocked.Exchange(ref mcpBridgeBusy, 0); }
                }));
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref mcpBridgeBusy, 0);
                throw;
            }

            bool finished;
            try { finished = done.Task.Wait(timeoutMs); }
            catch (AggregateException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                McpBridgeException bridgeError = inner as McpBridgeException;
                if (bridgeError != null) throw new McpBridgeException(bridgeError.Code, bridgeError.Message);
                Logger.WriteLine("MCP bridge command '" + cmd + "' failed: " + inner);
                throw new McpBridgeException("command_failed", inner.GetType().Name + ": " + inner.Message);
            }
            if (!finished)
            {
                bool dialog = false;
                try { dialog = !IsWindowEnabled(Handle); } catch (Exception) { }
                throw new McpBridgeException("timeout", dialog
                    ? "The editor is showing a dialog and is waiting for the user to answer it. The command continues after that."
                    : "The editor did not finish the command in time. It may still be working.");
            }
            return done.Task.Result;
        }

        private static readonly string[] McpBridgeCommands =
        {
            "ping", "get_state", "get_models", "get_project", "list_images", "list_types", "describe_type", "get_preview_values",
            "set_project", "open_project", "new_project", "save_project", "reload_images", "set_view", "set_preview_values",
            "render_preview", "pack"
        };

        private async Task<JToken> McpBridge_Execute(string cmd, JObject args)
        {
            switch (cmd)
            {
                // read only
                case "ping":
                case "get_state": return McpBridge_State();
                case "get_models": return McpBridge_Models();
                case "get_project": return McpBridge_GetProject();
                case "list_images": return McpBridge_ListImages();
                case "list_types": return McpBridge_ListTypes();
                case "describe_type": return McpBridge_DescribeType(args);
                case "get_preview_values": return McpBridge_GetPreviewValues(args);
            }

            // Everything below changes the editor state: refuse while the user has a dialog open.
            if (!IsWindowEnabled(Handle))
                throw new McpBridgeException("dialog_open", "A dialog is open in the editor. Close it and try again.");

            switch (cmd)
            {
                case "set_project": return McpBridge_SetProject(args);
                case "open_project": return McpBridge_OpenProject(args);
                case "new_project": return McpBridge_NewProject(args);
                case "save_project": return McpBridge_SaveProject();
                case "reload_images": return McpBridge_ReloadImages();
                case "set_view": return McpBridge_SetView(args);
                case "set_preview_values": return McpBridge_SetPreviewValues(args);
                case "render_preview": return McpBridge_RenderPreview(args);
                case "pack": return await McpBridge_Pack(args);
            }
            throw new McpBridgeException("unknown_command", "Unknown command: " + cmd);
        }

        // ---------------------------------------------------------------- helpers

        private static JsonSerializerSettings McpBridge_JsonSettings()
        {
            // same settings the editor uses to read and write the project file
            return new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
        }

        private bool McpBridge_HasProject()
        {
            return Watch_Face != null && !string.IsNullOrEmpty(ProjectDir) && !string.IsNullOrEmpty(FileName);
        }

        private void McpBridge_RequireProject()
        {
            if (!McpBridge_HasProject())
                throw new McpBridgeException("no_project", "No project is open in the editor. Use open_project or new_project first.");
        }

        private void McpBridge_RequireSaved(JObject args)
        {
            bool discard = (bool?)args["discard_changes"] ?? false;
            if (JSON_Modified && McpBridge_HasProject() && !discard)
                throw new McpBridgeException("unsaved_changes", "The open project has unsaved changes. " +
                    "Call save_project first, or pass discard_changes=true to drop them.");
        }

        private static string McpBridge_AbsolutePath(JObject args, string name, string extension)
        {
            string path = (string)args[name];
            if (string.IsNullOrWhiteSpace(path)) throw new McpBridgeException("bad_args", "Missing \"" + name + "\".");
            if (!Path.IsPathRooted(path)) throw new McpBridgeException("bad_args", "\"" + name + "\" must be an absolute path.");
            path = Path.GetFullPath(path);
            if (extension != null && !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new McpBridgeException("bad_args", "\"" + name + "\" must end with " + extension + ".");
            return path;
        }

        private UCtrl_Set McpBridge_PreviewSet(int number)
        {
            switch (number)
            {
                case 1: return userCtrl_Set1;
                case 2: return userCtrl_Set2;
                case 3: return userCtrl_Set3;
                case 4: return userCtrl_Set4;
                case 5: return userCtrl_Set5;
                case 6: return userCtrl_Set6;
                case 7: return userCtrl_Set7;
                case 8: return userCtrl_Set8;
                case 9: return userCtrl_Set9;
                case 10: return userCtrl_Set10;
                case 11: return userCtrl_Set11;
                case 12: return userCtrl_Set12;
            }
            throw new McpBridgeException("bad_args", "\"set\" must be between 1 and 12.");
        }

        private int McpBridge_ActivePreviewSet()
        {
            int number = WatchFacePreviewSet != null ? WatchFacePreviewSet.SetNumber : 1;
            return (number >= 1 && number <= 12) ? number : 1;
        }

        // elementName (as written in the project file) -> model class
        private static Dictionary<string, Type> mcpBridgeElementTypes;

        /// <summary>The types of the editor assembly; never throws when some type cannot be loaded.</summary>
        private static Type[] McpBridge_AssemblyTypes()
        {
            try { return typeof(WATCH_FACE).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        }

        private static Dictionary<string, Type> McpBridge_ElementTypes()
        {
            if (mcpBridgeElementTypes != null) return mcpBridgeElementTypes;
            Dictionary<string, Type> map = new Dictionary<string, Type>();
            foreach (Type type in McpBridge_AssemblyTypes())
            {
                if (!type.IsClass || type.IsAbstract || type.Namespace != typeof(WATCH_FACE).Namespace) continue;
                // Elements of a screen are the "Element..." classes; TopImage also has an elementName
                // but lives at the root of the project, not in an element list.
                if (!type.Name.StartsWith("Element", StringComparison.Ordinal)) continue;
                FieldInfo field = type.GetField("elementName", BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.FieldType != typeof(string)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                try
                {
                    string name = field.GetValue(Activator.CreateInstance(type)) as string;
                    if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name)) map.Add(name, type);
                }
                catch (Exception) { }
            }
            mcpBridgeElementTypes = map;
            return map;
        }

        // ---------------------------------------------------------------- read only commands

        private JObject McpBridge_State()
        {
            JObject project = new JObject();
            project["open"] = McpBridge_HasProject();
            if (!string.IsNullOrEmpty(ProjectDir)) project["dir"] = ProjectDir;
            if (!string.IsNullOrEmpty(FileName)) project["file"] = FileName;
            if (McpBridge_HasProject()) project["path"] = Path.Combine(ProjectDir, FileName);
            project["modified"] = JSON_Modified;
            if (Watch_Face != null && Watch_Face.WatchFace_Info != null)
            {
                WatchFace_Info info = Watch_Face.WatchFace_Info;
                if (info.WatchFaceName != null) project["name"] = info.WatchFaceName;
                project["id"] = info.WatchFaceId;
                project["version"] = info.WatchFaceVersion;
                if (info.DeviceName != null) project["device"] = info.DeviceName;
                if (info.Preview != null) project["preview_image"] = info.Preview;
            }
            int normal = 0, aod = 0;
            if (Watch_Face != null && Watch_Face.ScreenNormal != null && Watch_Face.ScreenNormal.Elements != null)
                normal = Watch_Face.ScreenNormal.Elements.Count;
            if (Watch_Face != null && Watch_Face.ScreenAOD != null && Watch_Face.ScreenAOD.Elements != null)
                aod = Watch_Face.ScreenAOD.Elements.Count;
            project["elements_normal"] = normal;
            project["elements_aod"] = aod;

            JObject model = new JObject();
            if (SelectedModel != null)
            {
                model["name"] = ProgramSettings != null ? ProgramSettings.Watch_Model : SelectedModel.name;
                model["width"] = SelectedModel.background.w;
                model["height"] = SelectedModel.background.h;
                model["screen_type"] = SelectedModel.screenType;
                model["design_width"] = SelectedModel.designWidth;
                model["os_version"] = SelectedModel.versionOS;
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version; // ("Version" alone is a class of the editor)
            JObject state = new JObject();
            state["protocol"] = McpBridgeServer.ProtocolVersion;
            state["editor_version"] = version.Major + "." + version.Minor + "." + version.Build;
            state["pid"] = Process.GetCurrentProcess().Id;
            state["editor_dir"] = Application.StartupPath;
            state["window_title"] = Text;
            state["language"] = CultureInfo.CurrentUICulture.Name;
            state["project"] = project;
            state["model"] = model;
            state["screen"] = radioButton_ScreenNormal.Checked ? "normal" : "aod";
            state["preview_set"] = McpBridge_ActivePreviewSet();
            state["images"] = ListImages.Count;
            state["dialog_open"] = !IsWindowEnabled(Handle);
            state["commands"] = new JArray(McpBridgeCommands);
            return state;
        }

        private JToken McpBridge_Models()
        {
            JArray models = new JArray();
            foreach (KeyValuePair<string, Classes.AmazfitPlatform> pair in AvailableConfigurations)
            {
                JObject model = new JObject();
                model["name"] = pair.Key;
                model["width"] = pair.Value.background.w;
                model["height"] = pair.Value.background.h;
                model["screen_type"] = pair.Value.screenType;
                model["design_width"] = pair.Value.designWidth;
                model["os_version"] = pair.Value.versionOS;
                models.Add(model);
            }
            JObject result = new JObject();
            result["selected"] = ProgramSettings != null ? ProgramSettings.Watch_Model : null;
            result["models"] = models;
            return result;
        }

        private JToken McpBridge_GetProject()
        {
            McpBridge_RequireProject();
            JObject result = new JObject();
            result["project"] = JObject.FromObject(Watch_Face, JsonSerializer.Create(McpBridge_JsonSettings()));
            result["state"] = McpBridge_State();
            return result;
        }

        private JToken McpBridge_ListImages()
        {
            McpBridge_RequireProject();
            JObject result = new JObject();
            result["dir"] = ProjectDir + @"\assets";
            result["images"] = new JArray(ListImages.ToArray());           // in the editor's own order
            result["animation_images"] = new JArray(ListAnimImages.ToArray());
            return result;
        }

        private JToken McpBridge_ListTypes()
        {
            JArray elements = new JArray();
            foreach (KeyValuePair<string, Type> pair in McpBridge_ElementTypes().OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                JObject item = new JObject();
                item["elementName"] = pair.Key;
                item["class"] = pair.Value.Name;
                elements.Add(item);
            }
            JObject result = new JObject();
            result["root"] = typeof(WATCH_FACE).Name;
            result["elements"] = elements;
            return result;
        }

        private static Type McpBridge_FindModelType(string name)
        {
            Type found;
            if (McpBridge_ElementTypes().TryGetValue(name, out found)) return found;
            foreach (Type type in McpBridge_AssemblyTypes())
            {
                if (type.Namespace != typeof(WATCH_FACE).Namespace || !type.IsPublic) continue;
                if (type.Name == name && (type.IsClass || type.IsEnum) && !typeof(Control).IsAssignableFrom(type)) return type;
            }
            return null;
        }

        private static bool McpBridge_IsModelClass(Type type)
        {
            return type.IsClass && type != typeof(string) && type.Namespace == typeof(WATCH_FACE).Namespace &&
                   !typeof(Control).IsAssignableFrom(type);
        }

        private static string McpBridge_TypeName(Type type, out string reference)
        {
            reference = null;
            Type nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null) return McpBridge_TypeName(nullable, out reference) + "?";
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(int) || type == typeof(short) || type == typeof(byte)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "float";
            if (type == typeof(object)) return "any";
            if (type.IsEnum) { reference = type.Name; return "enum " + type.Name; }
            if (type.IsArray)
            {
                string itemName = McpBridge_TypeName(type.GetElementType(), out reference);
                return "list<" + itemName + ">";
            }
            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                if (arguments.Length == 1 && typeof(IEnumerable).IsAssignableFrom(type))
                {
                    string itemName = McpBridge_TypeName(arguments[0], out reference);
                    return "list<" + itemName + ">";
                }
                if (arguments.Length == 2 && typeof(IDictionary).IsAssignableFrom(type))
                {
                    string keyReference;
                    return "map<" + McpBridge_TypeName(arguments[0], out keyReference) + ", " +
                           McpBridge_TypeName(arguments[1], out reference) + ">";
                }
            }
            if (McpBridge_IsModelClass(type)) reference = type.Name;
            return type.Name;
        }

        /// <summary>Lists the JSON members of one model class, read from the editor's own classes.</summary>
        private JToken McpBridge_DescribeType(JObject args)
        {
            string name = (string)args["name"];
            if (string.IsNullOrWhiteSpace(name)) throw new McpBridgeException("bad_args", "Missing \"name\".");
            Type type = McpBridge_FindModelType(name);
            if (type == null) throw new McpBridgeException("unknown_type", "Unknown model type: " + name);

            JObject result = new JObject();
            result["class"] = type.Name;
            if (type.IsEnum)
            {
                result["enum"] = new JArray(Enum.GetNames(type));
                return result;
            }

            object instance = null;
            try { instance = Activator.CreateInstance(type); }
            catch (Exception) { }

            JArray members = new JArray();
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            {
                FieldInfo field = member as FieldInfo;
                PropertyInfo property = member as PropertyInfo;
                Type memberType;
                object defaultValue = null;
                if (field != null)
                {
                    if (field.IsStatic) continue;
                    memberType = field.FieldType;
                    if (instance != null) defaultValue = field.GetValue(instance);
                }
                else if (property != null && property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    memberType = property.PropertyType;
                    try { if (instance != null) defaultValue = property.GetValue(instance, null); }
                    catch (Exception) { }
                }
                else continue;
                if (member.IsDefined(typeof(JsonIgnoreAttribute), true)) continue;

                string reference;
                JObject item = new JObject();
                item["name"] = member.Name;
                item["type"] = McpBridge_TypeName(memberType, out reference);
                if (reference != null) item["describe"] = reference; // can be passed to describe_type
                if (defaultValue != null && (memberType.IsPrimitive || memberType == typeof(string) || memberType.IsEnum))
                    item["default"] = JToken.FromObject(defaultValue);
                members.Add(item);
            }
            result["members"] = members;
            if (instance != null)
            {
                FieldInfo elementName = type.GetField("elementName", BindingFlags.Public | BindingFlags.Instance);
                if (elementName != null) result["elementName"] = elementName.GetValue(instance) as string;
            }
            return result;
        }

        private JToken McpBridge_GetPreviewValues(JObject args)
        {
            int number = args["set"] != null ? (int)args["set"] : McpBridge_ActivePreviewSet();
            JObject result = new JObject();
            result["set"] = number;
            result["active_set"] = McpBridge_ActivePreviewSet();
            result["values"] = JObject.Parse(McpBridge_PreviewSet(number).GetValue());
            return result;
        }

        // ---------------------------------------------------------------- project commands

        /// <summary>
        /// Checks a project the same way the editor will read it, but without message boxes.
        /// "problems" would make the editor show an error; "unknown" lists elements the editor would silently drop.
        /// </summary>
        private void McpBridge_ValidateProject(JObject project, List<string> problems, List<string> unknown, List<string> warnings)
        {
            JsonSerializer serializer = JsonSerializer.Create(McpBridge_JsonSettings());
            try { project.ToObject<WATCH_FACE>(serializer); }
            catch (Exception ex) { problems.Add("project: " + ex.Message); }

            McpBridge_ValidateElements(project.SelectToken("ScreenNormal.Elements"), "ScreenNormal.Elements", serializer, problems, unknown, warnings);
            McpBridge_ValidateElements(project.SelectToken("ScreenAOD.Elements"), "ScreenAOD.Elements", serializer, problems, unknown, warnings);
            JArray groups = project.SelectToken("Editable_Elements.Watchface_edit_group") as JArray;
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    JObject group = groups[i] as JObject;
                    if (group == null) continue;
                    McpBridge_ValidateElements(group["Elements"], "Editable_Elements.Watchface_edit_group[" + i + "].Elements",
                        serializer, problems, unknown, warnings);
                }
            }
        }

        private static void McpBridge_ValidateElements(JToken token, string path, JsonSerializer serializer,
            List<string> problems, List<string> unknown, List<string> warnings)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            JArray elements = token as JArray;
            if (elements == null) { problems.Add(path + ": must be a list."); return; }
            Dictionary<string, Type> types = McpBridge_ElementTypes();
            HashSet<Type> seen = new HashSet<Type>();
            for (int i = 0; i < elements.Count; i++)
            {
                string where = path + "[" + i + "]";
                JObject element = elements[i] as JObject;
                if (element == null) { problems.Add(where + ": must be an object."); continue; }
                JProperty nameProperty = element.Property("elementName");
                string name = nameProperty != null && nameProperty.Value.Type == JTokenType.String ? (string)nameProperty.Value : null;
                Type type;
                if (name == null || !types.TryGetValue(name, out type))
                {
                    unknown.Add(where + ": unknown elementName \"" + name + "\" (the editor drops elements it does not know).");
                    continue;
                }
                // The editor finds the element type by the first "elementName" text inside the element,
                // so keep that member first, as the editor itself writes it.
                if (element.First != nameProperty)
                {
                    nameProperty.Remove();
                    element.AddFirst(nameProperty);
                }
                try { element.ToObject(type, serializer); }
                catch (Exception ex) { problems.Add(where + " (" + name + "): " + ex.Message); }
                if (!seen.Add(type)) warnings.Add(where + ": a second \"" + name + "\" on the same screen; the editor shows one per type.");
            }
        }

        private JToken McpBridge_SetProject(JObject args)
        {
            McpBridge_RequireProject();
            JObject project = args["project"] as JObject;
            if (project == null && args["json"] != null)
            {
                try { project = JObject.Parse((string)args["json"]); }
                catch (Exception ex) { throw new McpBridgeException("invalid_project", "The project JSON is not valid: " + ex.Message); }
            }
            if (project == null) throw new McpBridgeException("bad_args", "Missing \"project\" (object).");

            List<string> warnings = new List<string>();
            List<string> problems = new List<string>();
            McpBridge_ValidateProject(project, problems, problems, warnings); // here an unknown element is an error too
            if (problems.Count > 0)
                throw new McpBridgeException("invalid_project", "The project was not applied:\n- " + string.Join("\n- ", problems.ToArray()));

            WATCH_FACE model = TextToJson(project.ToString(Formatting.None));
            if (model == null) throw new McpBridgeException("invalid_project", "The editor could not read the project.");
            // The editor drops what it does not recognise without saying so: make sure nothing was lost.
            string lost = McpBridge_LostElements(project.SelectToken("ScreenNormal.Elements") as JArray,
                              model.ScreenNormal != null ? model.ScreenNormal.Elements : null, "ScreenNormal") +
                          McpBridge_LostElements(project.SelectToken("ScreenAOD.Elements") as JArray,
                              model.ScreenAOD != null ? model.ScreenAOD.Elements : null, "ScreenAOD");
            if (lost.Length > 0) throw new McpBridgeException("invalid_project", "The project was not applied:" + lost);
            if (model.WatchFace_Info == null)
                model.WatchFace_Info = (Watch_Face != null && Watch_Face.WatchFace_Info != null) ? Watch_Face.WatchFace_Info : new WatchFace_Info();
            if (model.WatchFace_Info.DeviceName != null && !AvailableConfigurations.ContainsKey(model.WatchFace_Info.DeviceName))
                warnings.Add("WatchFace_Info.DeviceName \"" + model.WatchFace_Info.DeviceName + "\" is not a model name of this editor; " +
                    "the editor reads it as \"" + McpBridge_DeviceName(model) + "\".");

            McpBridge_ShowModel(model, (bool?)args["reload_images"] ?? false, true);

            JObject result = new JObject();
            result["state"] = McpBridge_State();
            if (warnings.Count > 0) result["warnings"] = new JArray(warnings.ToArray());
            return result;
        }

        /// <summary>Says which elements of a screen the editor did not keep when it read the project ("" when none).</summary>
        private static string McpBridge_LostElements(JArray given, List<object> kept, string screen)
        {
            if (given == null) return "";
            int expected = 0;
            foreach (JToken token in given)
            {
                // shortcuts written inside the list are moved by the editor to the root of the project
                if ((string)token["elementName"] != "ElementShortcuts") expected++;
            }
            int found = kept != null ? kept.Count : 0;
            if (found >= expected) return "";
            List<string> names = new List<string>();
            if (kept != null) foreach (object element in kept) names.Add(element.GetType().Name);
            return "\n- " + screen + ".Elements: the editor kept " + found + " of " + expected + " elements (" +
                   string.Join(", ", names.ToArray()) + ").";
        }

        /// <summary>Editor model name for the project's DeviceName (old names are mapped, unknown ones fall back like in LoadJson).</summary>
        private string McpBridge_DeviceName(WATCH_FACE model)
        {
            WATCH_FACE current = Watch_Face;
            try
            {
                Watch_Face = model; // GetNewFormatDeviceName reads the old names from Watch_Face
                return GetNewFormatDeviceName(model.WatchFace_Info.DeviceName);
            }
            catch (Exception) { return null; }
            finally { Watch_Face = current; }
        }

        /// <summary>Puts a model on screen following the same steps LoadJson uses after reading the file.</summary>
        private void McpBridge_ShowModel(WATCH_FACE model, bool reloadImages, bool modified)
        {
            Watch_Face = model;

            if (Watch_Face.WatchFace_Info != null && Watch_Face.WatchFace_Info.Preview != null)
            {
                button_RefreshPreview.Visible = true;
                button_CreatePreview.Visible = false;
            }
            else
            {
                button_RefreshPreview.Visible = false;
                button_CreatePreview.Visible = (FileName != null && ProjectDir != null);
            }

            PreviewView = false;
            if (reloadImages)
            {
                string dirName = ProjectDir + @"\assets\";
                LoadImage(dirName);
                LoadFonts(dirName);
            }
            LastColor.last_color = null;
            ShowElemetsWatchFace();
            if (Watch_Face.WatchFace_Info != null && Watch_Face.WatchFace_Info.DeviceName != null)
            {
                string device = McpBridge_DeviceName(Watch_Face);
                if (device != null) comboBox_watch_model.Text = device;
            }
            WidgetProperty.Clear();
            PreviewView = true;

            JSON_Modified = modified;
            PreviewImage();
            FormText();
        }

        private JToken McpBridge_OpenProject(JObject args)
        {
            string path = McpBridge_AbsolutePath(args, "path", ".json");
            if (!File.Exists(path)) throw new McpBridgeException("not_found", "File not found: " + path);
            McpBridge_RequireSaved(args);
            return McpBridge_Load(path);
        }

        private JToken McpBridge_Load(string path)
        {
            // Read first, so a broken file is reported here instead of in a message box.
            JObject project;
            try { project = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { throw new McpBridgeException("invalid_project", "Not a valid project file: " + ex.Message); }
            List<string> warnings = new List<string>();
            List<string> problems = new List<string>();
            McpBridge_ValidateProject(project, problems, warnings, warnings); // old projects may hold elements the editor drops
            if (problems.Count > 0)
                throw new McpBridgeException("invalid_project", "The editor would report errors in this file, so the bridge did not open it " +
                    "(open it in the editor by hand to see them):\n- " + string.Join("\n- ", problems.ToArray()));

            // Like the editor's "Open project": the autosave copy of the project that is being closed goes away
            // (it was either saved or its changes were dropped on purpose).
            bool sameProject = McpBridge_HasProject() &&
                string.Equals(Path.Combine(ProjectDir, FileName), path, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(ProjectDir) && !string.IsNullOrEmpty(FileName))
            {
                try
                {
                    string previousAutosave = Path.Combine(ProjectDir, FileName) + ".temp";
                    if (File.Exists(previousAutosave)) File.Delete(previousAutosave);
                }
                catch (Exception) { }
            }
            bool autosaveFound = !sameProject && File.Exists(path + ".temp");

            FileName = Path.GetFileName(path);
            ProjectDir = Path.GetDirectoryName(path);

            // LoadJson may ask two questions (restore the autosave, load Preview.States). The bridge cannot
            // answer a dialog, so both are skipped: the saved project file is opened as it is.
            bool autoSave = ProgramSettings.AutoSave;
            bool openDialog = ProgramSettings.Settings_Open_Dialog;
            try
            {
                ProgramSettings.AutoSave = false;
                ProgramSettings.Settings_Open_Dialog = false;
                LoadJson(path);
            }
            finally
            {
                ProgramSettings.AutoSave = autoSave;
                ProgramSettings.Settings_Open_Dialog = openDialog;
            }

            JObject result = new JObject();
            result["state"] = McpBridge_State();
            if (autosaveFound) result["autosave_found"] = true; // "<project>.json.temp" exists and was NOT restored
            if (warnings.Count > 0) result["warnings"] = new JArray(warnings.ToArray());
            return result;
        }

        private JToken McpBridge_NewProject(JObject args)
        {
            string dir = McpBridge_AbsolutePath(args, "dir", null);
            McpBridge_RequireSaved(args);

            string name = (string)args["name"];
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new McpBridgeException("bad_args", "Invalid project name.");

            string modelName = (string)args["model"];
            if (string.IsNullOrWhiteSpace(modelName)) modelName = ProgramSettings.Watch_Model;
            Classes.AmazfitPlatform platform;
            if (!AvailableConfigurations.TryGetValue(modelName, out platform))
                throw new McpBridgeException("unknown_model", "Unknown watch model: " + modelName + ". Use get_models to list them.");

            string path = Path.Combine(dir, name + ".json");
            if (File.Exists(path)) throw new McpBridgeException("already_exists", "The project file already exists: " + path);
            bool assetsExisted = Directory.Exists(Path.Combine(dir, "assets"));

            // Same starting project the "Create a new project" button writes.
            WATCH_FACE model = new WATCH_FACE();
            model.WatchFace_Info = new WatchFace_Info();
            model.WatchFace_Info.WatchFaceId = new Random().Next(1000, 10000000);
            model.WatchFace_Info.DeviceName = modelName;
            model.ScreenNormal = new ScreenNormal();
            model.ScreenNormal.Background = new Background();
            model.ScreenNormal.Background.BackgroundColor = new hmUI_widget_FILL_RECT();
            model.ScreenNormal.Background.BackgroundColor.color = "0xFF000000";
            model.ScreenNormal.Background.BackgroundColor.x = 0;
            model.ScreenNormal.Background.BackgroundColor.y = 0;
            model.ScreenNormal.Background.BackgroundColor.w = platform.background.w;
            model.ScreenNormal.Background.BackgroundColor.h = platform.background.h;

            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            File.WriteAllText(path, JsonConvert.SerializeObject(model, Formatting.Indented, McpBridge_JsonSettings()), Encoding.UTF8);

            JObject result = (JObject)McpBridge_Load(path);
            if (assetsExisted)
            {
                JArray warnings = result["warnings"] as JArray;
                if (warnings == null) { warnings = new JArray(); result["warnings"] = warnings; }
                warnings.Add("The folder already had an \"assets\" folder; its images are now part of the new project.");
            }
            return result;
        }

        private JToken McpBridge_SaveProject()
        {
            McpBridge_RequireProject();
            string path = Path.Combine(ProjectDir, FileName);
            save_JSON_File(path);
            JSON_Modified = false;
            FormText();
            JObject result = new JObject();
            result["path"] = path;
            result["state"] = McpBridge_State();
            return result;
        }

        private JToken McpBridge_ReloadImages()
        {
            McpBridge_RequireProject();
            string dirName = ProjectDir + @"\assets\";
            LoadImage(dirName);
            LoadFonts(dirName);
            PreviewImage();
            return McpBridge_ListImages();
        }

        private JToken McpBridge_SetView(JObject args)
        {
            string screen = ((string)args["screen"] ?? "").ToLowerInvariant();
            if (screen == "normal") radioButton_ScreenNormal.Checked = true;
            else if (screen == "aod") radioButton_ScreenIdle.Checked = true;
            else throw new McpBridgeException("bad_args", "\"screen\" must be \"normal\" or \"aod\".");
            return McpBridge_State();
        }

        // ---------------------------------------------------------------- preview

        private JToken McpBridge_SetPreviewValues(JObject args)
        {
            int number = args["set"] != null ? (int)args["set"] : McpBridge_ActivePreviewSet();
            UCtrl_Set target = McpBridge_PreviewSet(number);
            JObject values = args["values"] as JObject;
            if (values == null) throw new McpBridgeException("bad_args", "Missing \"values\" (object).");

            JObject merged = JObject.Parse(target.GetValue());
            merged.Merge(values, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            target.SetValue(merged.ToString(Formatting.None));

            bool activate = (bool?)args["activate"] ?? true;
            if (activate)
            {
                // same as clicking the "Values N" header: this set is open, the others are collapsed
                for (int i = 1; i <= 12; i++) McpBridge_PreviewSet(i).Collapsed = (i != number);
                WatchFacePreviewSet.SetNumber = number;
            }
            if (activate || number == McpBridge_ActivePreviewSet())
            {
                SetPreferences(target);
                PreviewImage();
            }
            return McpBridge_GetPreviewValues(args);
        }

        private JToken McpBridge_RenderPreview(JObject args)
        {
            if (Watch_Face == null) throw new McpBridgeException("no_project", "No project is open in the editor.");

            string screen = ((string)args["screen"] ?? "").ToLowerInvariant();
            int link;
            if (screen == "normal") link = 0;
            else if (screen == "aod") link = 1;
            else if (screen == "") link = radioButton_ScreenNormal.Checked ? 0 : 1;
            else throw new McpBridgeException("bad_args", "\"screen\" must be \"normal\" or \"aod\".");

            bool mask = (bool?)args["mask"] ?? false;
            bool skin = (bool?)args["skin"] ?? false;
            JObject overlays = args["overlays"] as JObject;
            if (overlays == null) overlays = new JObject();
            Func<string, bool> overlay = key => (bool?)overlays[key] ?? false;

            string path;
            if (args["path"] != null && !string.IsNullOrWhiteSpace((string)args["path"]))
            {
                path = McpBridge_AbsolutePath(args, "path", ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
            }
            else
            {
                string dir = Path.Combine(Path.GetTempPath(), "wfe-mcp");
                Directory.CreateDirectory(dir);
                mcpBridgePreviewCount++;
                path = Path.Combine(dir, "preview-" + Process.GetCurrentProcess().Id + "-" + mcpBridgePreviewCount + ".png");
            }

            int activeSet = McpBridge_ActivePreviewSet();
            int renderSet = args["set"] != null ? (int)args["set"] : activeSet;
            UCtrl_Set renderValues = McpBridge_PreviewSet(renderSet);

            JObject result = new JObject();
            Bitmap bitmap = new Bitmap(SelectedModel.background.w, SelectedModel.background.h, PixelFormat.Format32bppArgb);
            try
            {
                if (renderSet != activeSet) SetPreferences(renderValues);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    // same call the "Save PNG" button makes, plus the optional helper overlays
                    Preview_screen(graphics, 1.0f, false, overlay("grid_white"), overlay("grid_black"), overlay("border"),
                        overlay("shortcuts"), overlay("shortcuts"), overlay("shortcuts"), false,
                        overlay("buttons"), overlay("buttons"), overlay("buttons"),
                        true, overlay("progress_area"), overlay("center_marker"), overlay("widgets_area"),
                        link, false, false, -1, false, 0);
                }
                Bitmap plain = bitmap;
                if (skin) bitmap = ApplyWatchSkin(plain);
                else if (mask)
                {
                    using (Bitmap maskImage = new Bitmap(Application.StartupPath + @"\Mask\" + SelectedModel.maskImage))
                    {
                        bitmap = ApplyMask(plain, maskImage);
                    }
                }
                if (!ReferenceEquals(plain, bitmap)) plain.Dispose();
                bitmap.Save(path, ImageFormat.Png);
                result["path"] = path;
                result["width"] = bitmap.Width;
                result["height"] = bitmap.Height;
                result["screen"] = link == 0 ? "normal" : "aod";
                result["set"] = renderSet;
            }
            finally
            {
                bitmap.Dispose();
                // the editor keeps showing the values the user had selected
                if (renderSet != activeSet) SetPreferences(McpBridge_PreviewSet(activeSet));
            }
            return result;
        }

        // ---------------------------------------------------------------- pack

        private async Task<JToken> McpBridge_Pack(JObject args)
        {
            McpBridge_RequireProject();
            if (JSON_Modified)
            {
                bool saveFirst = (bool?)args["save_first"] ?? true;
                if (!saveFirst)
                    throw new McpBridgeException("unsaved_changes", "The project has unsaved changes. Save it first or pass save_first=true.");
                save_JSON_File(Path.Combine(ProjectDir, FileName));
                JSON_Modified = false;
                FormText();
            }

            // same file name the "Pack watch face" button uses
            string baseName = Path.GetFileNameWithoutExtension(FileName);
            if (Watch_Face.WatchFace_Info != null && !string.IsNullOrEmpty(Watch_Face.WatchFace_Info.WatchFaceName))
                baseName = Watch_Face.WatchFace_Info.WatchFaceName;
            string zipName = ProjectDir + @"\" + baseName + ".zip";
            bool createZpk = args["zpk"] != null ? (bool)args["zpk"] : ProgramSettings.CreateZPK;

            JObject result = new JObject();
            try
            {
                zipName = await CreateWatchFace(zipName);
                if (string.IsNullOrEmpty(zipName) || !File.Exists(zipName))
                    throw new McpBridgeException("pack_failed", "The editor did not create the watch face file. Check the editor window for a message.");
                result["zip"] = zipName;
                if (createZpk)
                {
                    string zpkPath = CreateZPK(zipName);
                    if (!string.IsNullOrEmpty(zpkPath) && File.Exists(zpkPath)) result["zpk"] = zpkPath;
                }
            }
            finally
            {
#if !DEBUG
                try
                {
                    string tempDir = Application.StartupPath + @"\Temp";
                    if (Directory.Exists(tempDir)) DeleteDirectory(tempDir);
                }
                catch (Exception) { }
#endif
                progressBar1.Visible = false;
            }
            result["state"] = McpBridge_State();
            return result;
        }
    }
}
