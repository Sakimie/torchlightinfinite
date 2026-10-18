using System.Collections.ObjectModel;

namespace Talos.Core;

/// <summary>Independently implemented, supported mechanics only. No external policy or runtime.</summary>
public static class EngineCatalog
{
    public const int TotalDays = 20;
    public const int RainbowTier = 5;
    public const int SupremeTier = 4;
    public const int InstigateEvents = 1;
    public const int MuzzlePopulation = 3;
    public const string StrategyVersion = "csharp-visible-policy-v1";
    public const string SimulationVersion = "csharp-twenty-day-simulator-v1";
    private static IReadOnlyDictionary<string, string> Map(params (string Id, string Name)[] items) =>
        new ReadOnlyDictionary<string, string>(items.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, string> Species { get; } = Map(
        ("mammoth", "巨象"), ("lion", "狂狮"), ("wolf", "头狼"), ("rat", "硕鼠"));
    public static IReadOnlyList<string> Tiers { get; } = Array.AsReadOnly(new[] { "普通", "魔法", "稀有", "传奇", "至臻", "绮彩（已入库）" });
    public static IReadOnlyDictionary<string, string> Kinds { get; } = Map(
        ("unknown", "未确认"), ("office", "审讯室"), ("locked", "未开门"),
        ("empty", "已开空房"), ("nest", "鼠巢"), ("inmate", "囚犯"));
    public static IReadOnlyDictionary<string, string> Phases { get; } = Map(
        ("patrol", "选择巡逻目的地"), ("manage", "吞噬、替换或结束"), ("office", "审讯室"),
        ("event", "处理事件"), ("tool", "选择刑具"), ("gift", "选择珍藏"),
        ("chain", "正在连吃"), ("ratway", "鼠道自由移动"));
    public static IReadOnlyDictionary<string, string> Tools { get; } = Map(
        ("coin", "手足的命运硬币"), ("shackles", "长链脚镣"), ("blacklist", "执行官的黑名册"),
        ("scapegoat", "替罪人的断头台"), ("maiden", "切开的人形棺"), ("rope", "绞首绳"),
        ("collar", "疯兽的倒刺项圈"), ("revenge", "复仇者之匣"), ("cage", "拥挤兽笼"),
        ("saw", "锈锯"), ("whip", "棘刺铁鞭"), ("skewer", "鼠头串：硕鼠"),
        ("muzzle", "狂兽的剧痛口枷"), ("tongs", "恶欲铁钳：巨象"), ("ironbox", "蠕动铁盒：硕鼠"),
        ("keys", "遗落的钥匙串"), ("fire", "灼痛的烙铁"), ("hook", "挑拣钩"),
        ("dogtag", "重刑铭牌"), ("stake", "火刑柱"));
    public static IReadOnlyDictionary<string, string> Events { get; } = Map(
        ("chain", "连锁反应"), ("execution", "处刑效率"), ("trickster", "暗箱操作"),
        ("appetite", "贪婪食欲"), ("convict", "高危囚犯"), ("crow", "鸦语"),
        ("freedom", "自由的滋味"), ("ratway", "暗渠鼠道"), ("hunt", "寻“宝”"),
        ("usurp1", "僭越·其一"), ("usurp2", "僭越·其二"),
        ("usurp3", "僭越·其三"), ("usurp4", "僭越·其四"));
    public static IReadOnlyDictionary<string, string> Gifts { get; } = Map(
        ("ring", "三首扳指"), ("horn", "斗争铜号"), ("wine", "赤色佳酿"), ("brand", "原罪烙印"));
    public static IReadOnlyDictionary<string, string> Objectives { get; } = Map(
        ("king", "一名至臻囚犯"), ("walk", "累计移动18格"), ("incident", "触发2次事件"),
        ("consume", "累计吞噬4次（阈值待确认）"), ("other", "其他任务（不估算奖励）"));
    public static IReadOnlyDictionary<string, string> Unsupported { get; } = Map(
        ("lion-trap", "染红的捕兽夹：狂狮"), ("trap", "主动寻猎"),
        ("shared-fortune", "有福同享"), ("nemesis", "宿敌"), ("shared-transfer", "同罪移押"),
        ("pestle", "铁杵"), ("crown", "狱王的铁冠：狂狮"), ("maul", "象足重锤：巨象"),
        ("ravenous", "凶兽的饥饿绞链"), ("guillotine", "狼牙铡刀：头狼"),
        ("scales", "典狱官的天平"), ("shortchain", "短链脚镣"), ("axe", "狱卒的行刑巨斧"),
        ("forged", "伪造钥匙"), ("scratch", "划痕吞噬"), ("prey", "猎物标记"),
        ("extra-action", "额外行动次数"), ("special-start", "特殊开局"));

    public static IReadOnlyList<double> OpenWeights { get; } = Array.AsReadOnly(new[] { 40d, 38, 12, 8, 2 });
    public static IReadOnlyList<double> ReplacementWeights { get; } = Array.AsReadOnly(new[] { 52d, 32, 9, 6, 1.5 });
    public static IReadOnlyList<string> RuleAssumptions { get; } = Array.AsReadOnly(new[]
    {
        "口枷人数按吞噬后、升阶前是否不超过三名判断，结算时点仍待实机确认。",
        "开门与替换阶级权重来自旧公开样本近似；已见但未知内容的事件按等概率估值，并非官方概率。",
        "硬币与批量效果只按可见局面近似估值，不模拟完整结算序列；吞噬任务4次阈值待确认。",
        "黑名册只估普通巡逻主开门；副开门不估主开门加成。鼠巢仍计入未开门数。",
        "处刑按基础克制的低阶目标估值，不继承传奇第二克制和鼠串扩展；此解释沿用既有参考规则，尚待实机确认。",
        "这是C#新实现的单步相对评分；未训练、未做整局收益验证，不继承旧版本的模拟收益或准确率。"
    });

    public static IReadOnlyList<string> SimulationAssumptions { get; } = Array.AsReadOnly(new[]
    {
        "整局模型固定普通20天；未来刑具仅从已支持的20种中生成，尚未覆盖游戏全部刑具、事件和特殊开局。",
        "四种族等概率，开门和替换阶级用旧公开样本近似；初始2事件、重封门及未看门5%潜在事件、事件类型等概率均为练习假设。",
        "硬币每天全场最多一次60%判定，在同种同阶配对中均匀选一对；铁盒每天一处鼠巢两天后孵化；铁钳每天随机标一名巨象。",
        "多效果按固定顺序、批量效果结算后再选所排队的奖励；新刑具不追溯触发当天已经结算的早晨效果。",
        "口枷按吞后、升阶前人口不超过三名；黑名册仅普通巡逻主开门；扳指、钥匙、铁烙复制、鼠巢及连锁路径的副开门抑制主开门加成。",
        "副开门保留事件标记，典狱长进入目的地时仍触发事件，此时序尚待实机确认；处刑采用基础克制的严格低阶解释。",
        "奖励移除当前事件囚犯后，已触发而未处理的事件仍保留；无囚犯的处刑/暗箱接受时无囚犯效果，全场事件照常。此恢复时序待实机确认。",
        "鸦语只揭稀有度；寻宝揭普通/魔法，其余门保留至少稀有的下界；高危囚犯事件保证所开囚犯至少传奇。",
        "后续决策只使用Observe提供的可见信息。蒙特卡洛误差不涵盖机制和概率假设误差，未完成或非法轨迹不计为终局结果。"
    });
}
