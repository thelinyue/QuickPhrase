using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// 只读的输入区能力检查器。单独使用原生 UIA COM，避免旧 .NET UIA 客户端注册的
// 代理回调使标准 Edit 被误报为没有文本接口的 Pane。只读取控件身份、状态和接口
// 可用性，不读取 Name／Value／Text，不激活窗口，也不操作组字、选区或剪贴板。
public static class ShanyuNativeInputMetadata {
    // 以下接口只声明查询所需的 vtable；未使用的方法保留槽位，禁止调用占位声明。
    [ComImport,Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Automation {
        void CompareElements();void CompareRuntimeIds();void GetRootElement();
        [PreserveSig] int ElementFromHandle(IntPtr handle,out Element element);
        void ElementFromPoint();void GetFocusedElement();void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();void ElementFromPointBuildCache();void GetFocusedElementBuildCache();
        void CreateTreeWalker();void ControlViewWalker();void ContentViewWalker();void RawViewWalker();
        void RawViewCondition();void ControlViewCondition();void ContentViewCondition();void CreateCacheRequest();
        [PreserveSig] int CreateTrueCondition(out IntPtr condition);
    }
    [ComImport,Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Element {
        void SetFocus();void GetRuntimeId();void FindFirst();
        [PreserveSig] int FindAll(int scope,IntPtr condition,out Elements elements);
        void FindFirstBuildCache();void FindAllBuildCache();void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int id,[MarshalAs(UnmanagedType.Struct)] out object value);
    }
    [ComImport,Guid("14314595-b4bc-4055-95f2-58f2e42c9855"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Elements {
        [PreserveSig] int Length(out int length);
        [PreserveSig] int GetElement(int index,out Element element);
    }
    static object Property(Element element,int id) {
        object value;Marshal.ThrowExceptionForHR(element.GetCurrentPropertyValue(id,out value));return value;
    }
    static bool? Flag(Element element,int id) {return Property(element,id) as bool?;}

    public static Dictionary<string,object>[] Read(long handle) {
        var instance=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9")));
        Element root=null;Elements elements=null;IntPtr condition=IntPtr.Zero;
        try {
            var automation=(Automation)instance;
            Marshal.ThrowExceptionForHR(automation.ElementFromHandle(new IntPtr(handle),out root));
            Marshal.ThrowExceptionForHR(automation.CreateTrueCondition(out condition));
            // Subtree 同时支持传入顶层窗口和直接传入编辑控件；自绘控件也按接口检查。
            Marshal.ThrowExceptionForHR(root.FindAll(7,condition,out elements));
            int count;Marshal.ThrowExceptionForHR(elements.Length(out count));
            var result=new List<Dictionary<string,object>>();
            for(int i=0;i<count;i++) {
                Element element=null;
                try {
                    Marshal.ThrowExceptionForHR(elements.GetElement(i,out element));
                    int type=Convert.ToInt32(Property(element,30003)); // UIA_ControlTypePropertyId
                    bool? value=Flag(element,30043),text=Flag(element,30040);
                    bool? text2=Flag(element,30119),textEdit=Flag(element,30149);
                    bool? focusable=Flag(element,30009);
                    bool customValue=(type==50025 || type==50033) && value==true && focusable==true;
                    if(type!=50004 && type!=50030 && !customValue && text!=true && text2!=true && textEdit!=true)continue;
                    result.Add(new Dictionary<string,object> {
                        {"AutomationId",Property(element,30011) as string},
                        {"ClassName",Property(element,30012) as string},
                        {"ControlType",type==50004?"ControlType.Edit":type==50030?"ControlType.Document":type.ToString()},
                        {"NativeWindowHandle",Property(element,30020)},
                        {"ProcessId",Property(element,30002)},
                        {"Enabled",Flag(element,30010)},
                        {"Offscreen",Flag(element,30022)},
                        {"Focusable",focusable},
                        {"Password",Flag(element,30019)},
                        {"ReadOnly",value==true?Flag(element,30046):null},
                        {"Bounds",Property(element,30001) as double[]},
                        {"ValuePattern",value},
                        {"TextPattern",text},
                        {"TextPattern2",text2},
                        {"TextEditPattern",textEdit}
                    });
                } finally {if(element!=null)Marshal.ReleaseComObject(element);}
            }
            return result.ToArray();
        } finally {
            if(elements!=null)Marshal.ReleaseComObject(elements);
            if(condition!=IntPtr.Zero)Marshal.Release(condition);
            if(root!=null)Marshal.ReleaseComObject(root);
            Marshal.ReleaseComObject(instance);
        }
    }
}
