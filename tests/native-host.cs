using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// 这是通用剪贴板验收宿主，不含产品窗口选择器或任何聊天软件的发送适配。
public static class ShanyuTestHost {
    [STAThread] public static void Main(string[] args) { Run(args[0],args[1]); }
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    static string eventsPath,controlPath,lastControl;
    static readonly List<Dictionary<string,object>> events = new List<Dictionary<string,object>>();
    static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    static Form form;
    static InputBox input;
    static TextBox alternate;
    static bool ctrlEnter;
    static DateTime releaseClipboard;
    static DataObject original;
    static uint ownClipboard;
    static void Record(string kind,object value) {events.Add(new Dictionary<string,object>{{"kind",kind},{"value",value}});Flush();}
    static void Flush() {
        var data=new Dictionary<string,object>{{"hwnd",form.Handle.ToInt64()},{"focus",input.Handle.ToInt64()},{"alternateFocus",alternate.Handle.ToInt64()},{"alternateDraft",alternate.Text},{"pid",System.Diagnostics.Process.GetCurrentProcess().Id},{"events",events},{"draft",input.Text},{"clipboardSequence",GetClipboardSequenceNumber()}};
        File.WriteAllText(eventsPath,json.Serialize(data),new System.Text.UTF8Encoding(false));
    }
    static void ProtectOriginalClipboard() {
        var source=Clipboard.GetDataObject();original=new DataObject();if(source==null)return;
        foreach(var format in source.GetFormats(false)) {
            object data=source.GetData(format,false);
            var image=data as Image;if(image!=null)data=image.Clone();
            var stream=data as MemoryStream;if(stream!=null)data=new MemoryStream(stream.ToArray());
            if(data!=null)original.SetData(format,false,data);
        }
    }
    public static void Run(string output,string controls) {
        eventsPath=output;controlPath=controls;ProtectOriginalClipboard();
        Application.EnableVisualStyles();
        form=new ShanyuDockForm{Text="闪语通用发送验收",Width=600,Height=450,StartPosition=FormStartPosition.CenterScreen};
        var label=new Label{Text="仅用于本机文字、图片、文件与发送按键验收",Dock=DockStyle.Top,Height=32};
        // 仅在宿主内禁用输入法：普通按键回放立即形成文字，不把输入法候选误判为没有回放。
        input=new InputBox{Name="ShanyuChatInput",Multiline=true,Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",12),Text="已有草稿：",ImeMode=ImeMode.Disable};
        alternate=new TextBox{Dock=DockStyle.Bottom,Text="另一输入位置（用于失焦验收）",Height=32,ImeMode=ImeMode.Disable};
        form.Controls.Add(input);form.Controls.Add(alternate);form.Controls.Add(label);
        // 快捷键回放和取消验收也记录普通输入，不能只观察剪贴板粘贴事件。
        input.TextChanged+=(sender,args)=>Flush();alternate.TextChanged+=(sender,args)=>Flush();
        var timer=new Timer{Interval=40};timer.Tick+=(sender,args)=>{
            if(releaseClipboard!=DateTime.MinValue && DateTime.Now>=releaseClipboard){CloseClipboard();releaseClipboard=DateTime.MinValue;Record("clipboard-released",true);}
            if(!File.Exists(controlPath))return;string text;
            try{text=File.ReadAllText(controlPath);}catch(IOException){return;}
            if(text==lastControl)return;lastControl=text;
            try{
                var command=json.Deserialize<Dictionary<string,object>>(text);var action=(string)command["command"];
                if(action=="reset") {events.Clear();input.Text=command.ContainsKey("draft")?(string)command["draft"]:"";ctrlEnter=command.ContainsKey("ctrlEnter")&&(bool)command["ctrlEnter"];input.SelectionStart=input.TextLength;Flush();}
                if(action=="focus") {form.WindowState=FormWindowState.Normal;form.Activate();SetForegroundWindow(form.Handle);input.Focus();input.SelectionStart=input.TextLength;Flush();}
                if(action=="blur") {alternate.Focus();Record("focus-changed",true);}
                if(action=="copy") {Clipboard.SetText((string)command["text"]);ownClipboard=GetClipboardSequenceNumber();Record("new-copy",true);}
                if(action=="copy-image") {using(var image=Image.FromFile((string)command["path"])){Clipboard.SetImage(image);}ownClipboard=GetClipboardSequenceNumber();Record("test-copy-image",true);}
                if(action=="copy-files") {var files=new System.Collections.Specialized.StringCollection();files.Add((string)command["path"]);Clipboard.SetFileDropList(files);ownClipboard=GetClipboardSequenceNumber();Record("test-copy-files",true);}
                if(action=="adopt") {if(GetClipboardSequenceNumber()==Convert.ToUInt32(command["sequence"]))ownClipboard=GetClipboardSequenceNumber();}
                if(action=="hold") {if(OpenClipboard(form.Handle)){releaseClipboard=DateTime.Now.AddMilliseconds(1200);Record("clipboard-held",true);}}
                if(action=="minimize") {form.WindowState=FormWindowState.Minimized;Record("minimized",true);}
                if(action=="close") {form.Close();}
            }catch(Exception e){Record("host-error",e.Message);}
        };
        form.Shown+=(sender,args)=>{input.Focus();input.SelectionStart=input.TextLength;Flush();timer.Start();};
        form.FormClosed+=(sender,args)=>{timer.Stop();if(releaseClipboard!=DateTime.MinValue)CloseClipboard();
            // 只恢复本宿主最后复制的测试内容；用户在此之后复制的内容会保留。
            if(ownClipboard!=0 && GetClipboardSequenceNumber()==ownClipboard && original!=null)Clipboard.SetDataObject(original,true);
        };
        Application.Run(form);timer.Dispose();
    }
    sealed class InputBox:TextBox {
        protected override bool ProcessCmdKey(ref Message msg,Keys key) {
            if(key==(Keys.Control|Keys.V)) {
                if(Clipboard.ContainsFileDropList()) {var paths=Clipboard.GetFileDropList();var values=new string[paths.Count];paths.CopyTo(values,0);Record("paste-files",values);}
                else if(Clipboard.ContainsImage()) {using(var image=Clipboard.GetImage()){Record("paste-image",new int[]{image.Width,image.Height});}}
                else if(Clipboard.ContainsText()) {var text=Clipboard.GetText();SelectedText=text;Record("paste-text",text);}
                else Record("unsupported-format",Clipboard.GetDataObject().GetFormats());
                return true;
            }
            if(key==Keys.Enter||key==(Keys.Control|Keys.Enter)) {
                var isCtrl=key==(Keys.Control|Keys.Enter);if(isCtrl==ctrlEnter){Record("send",new Dictionary<string,object>{{"text",Text},{"key",isCtrl?"Ctrl+Enter":"Enter"}});Clear();Flush();return true;}
            }
            return base.ProcessCmdKey(ref msg,key);
        }
    }
}
