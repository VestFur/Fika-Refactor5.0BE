/// <summary>
/// 该类用于指定某个设置应在 ConfigurationManager 设置窗口中以何种方式显示。
/// 
/// 用法：
/// 这份类模板必须复制到插件的项目内，并由其代码直接引用。
/// 新建一个实例，为你想要覆盖的字段赋值，然后把它作为 tag 传给你的设置。
/// 
/// 如果某个字段为 null（默认值），它会被忽略，不会改变设置的显示方式。
/// 如果某个字段非 null（你给它赋了值），它将覆盖默认行为。
/// </summary>
/// 
/// <example> 
/// 下面是一个示例，演示如何覆盖设置的顺序，以及把其中一个设置标记为高级：
/// <code>
/// // 覆盖 IsAdvanced 与 Order
/// Config.Bind("X", "1", 1, new ConfigDescription("", null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));
/// // 只覆盖 Order，IsAdvanced 保持 ConfigManager 赋的默认值
/// Config.Bind("X", "2", 2, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 1 }));
/// Config.Bind("X", "3", 3, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 2 }));
/// </code>
/// </example>
/// 
/// <remarks> 
/// 更多说明与示例见 readme：https://github.com/BepInEx/BepInEx.ConfigurationManager
/// 你也可以从这个类里删掉用不到的字段，效果与把它们留作 null 相同。
/// </remarks>
#pragma warning disable 0169, 0414, 0649
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>
    /// 该设置是否以百分比形式显示（仅用于带取值范围设置的情况）。
    /// </summary>
    public bool? ShowRangeAsPercent;

    /// <summary>
    /// 自定义设置编辑器（用 OnGUI 代码替换 ConfigurationManager 提供的默认编辑器）。
    /// 更详细的说明见下。使用自定义 drawer 会导致许多其他字段失效。
    /// </summary>
    public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;

    /// <summary>
    /// 允许用 Input（或 UnityInput）类轮询键盘输入的自定义设置编辑器。
    /// CustomDrawer 与 CustomHotkeyDrawer 只用其一，同时使用两者会导致未定义行为。
    /// </summary>
    public CustomHotkeyDrawerFunc CustomHotkeyDrawer;

    /// <summary>
    /// 允许用 Input 类轮询键盘输入的自定义设置绘制动作。
    /// 注意：在接受输入时务必把焦点放在你的 UI 控件上，以免用户把内容输进搜索框或其他设置里（最好每帧都这样做）。
    /// 如果你不绘制任何可选择 UI 控件，可以每帧执行 `GUIUtility.keyboardControl = -1;` 以确保没有任何东西被选中。
    /// </summary>
    /// <example>
    /// CustomHotkeyDrawer = (ConfigEntryBase setting, ref bool isEditing) =>
    /// {
    ///     if (isEditing)
    ///     {
    ///         // 确认没有别的控件被选中，因为我们并没有用 GUI.FocusControl 聚焦某个文本框。
    ///         GUIUtility.keyboardControl = -1;
    ///                     
    ///         // 此处使用 Input.GetKeyDown 等方法，记得在完成后把 isEditing 设为 false！
    ///         // 最好检查 Input.anyKeyDown，若为 true 就立刻把 isEditing 设为 false，
    ///         // 这样这次输入就没有机会传播到游戏本身。
    /// 
    ///         if (GUILayout.Button("Stop"))
    ///             isEditing = false;
    ///     }
    ///     else
    ///     {
    ///         if (GUILayout.Button("Start"))
    ///             isEditing = true;
    ///     }
    /// 
    ///     // 只有在 isEditing 为 true 且你按住任意键时，这里才会为 true
    ///     GUILayout.Label("Any key pressed: " + Input.anyKey);
    /// }
    /// </example>
    /// <param name="setting">
    /// 当前正在被设置的那个设置项（在可用时）。
    /// </param>
    /// <param name="isCurrentlyAcceptingInput">
    /// 当你希望当前设置 drawer 接收 Input 事件时，把这个 ref 参数设为 true。
    /// 该值在设置后会保持，可用它判断当前实例是否正在被编辑。
    /// 记得完成后把它设回 false！
    /// </param>
    public delegate void CustomHotkeyDrawerFunc(BepInEx.Configuration.ConfigEntryBase setting, ref bool isCurrentlyAcceptingInput);

    /// <summary>
    /// 是否在设置界面中显示这个设置？为 false 则不显示。
    /// </summary>
    public bool? Browsable;

    /// <summary>
    /// 该设置所属的分类。为 null 则直接挂在插件之下。
    /// </summary>
    public string Category;

    /// <summary>
    /// 若设置，设置的旁边会显示一个 "Default" 按钮，用于恢复默认值。
    /// </summary>
    public object DefaultValue;

    /// <summary>
    /// 强制不显示 "Reset" 按钮，即使存在有效的 DefaultValue。
    /// </summary>
    public bool? HideDefaultButton;

    /// <summary>
    /// 强制不显示设置的名称。只应与 <see cref="CustomDrawer"/> 搭配使用，以换取更多空间。
    /// 可以与 <see cref="HideDefaultButton"/> 一起使用，从而获得更多空间。
    /// </summary>
    public bool? HideSettingName;

    /// <summary>
    /// 鼠标悬停在设置上时显示的可选描述。
    /// 不推荐使用，改为在创建设置时提供描述。
    /// </summary>
    public string Description;

    /// <summary>
    /// 设置的名称。
    /// </summary>
    public string DispName;

    /// <summary>
    /// 该设置在分类内设置列表中的相对顺序。
    /// 默认为 0，数值越大在列表中越靠上。
    /// </summary>
    public int? Order;

    /// <summary>
    /// 只显示值，不允许编辑。
    /// </summary>
    public bool? ReadOnly;

    /// <summary>
    /// 若为 true，默认不显示该设置。用户必须开启显示高级设置或搜索它才能看到。
    /// </summary>
    public bool? IsAdvanced;

    /// <summary>
    /// 内置编辑器文本框所用的、从设置类型到字符串的自定义转换器。
    /// </summary>
    public System.Func<object, string> ObjToStr;

    /// <summary>
    /// 内置编辑器文本框所用的、从字符串到设置类型的自定义转换器。
    /// </summary>
    public System.Func<string, object> StrToObj;
}