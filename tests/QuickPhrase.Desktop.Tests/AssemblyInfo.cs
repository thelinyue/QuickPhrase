// Desktop 运行时控件测试共享进程唯一的 WPF Application、STA Dispatcher 和资源字典。
// 禁止测试类并行，避免资源与控件容器生成状态相互干扰。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
