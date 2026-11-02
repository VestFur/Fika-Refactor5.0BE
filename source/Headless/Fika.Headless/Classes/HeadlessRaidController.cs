﻿using System;

namespace Fika.Headless.Classes;

/// <summary>
/// 用于每帧同步变换，否则例如 <see cref="EFT.Interactive.TransitPoint"/> 之类的触发器就无法工作
/// </summary>
public class HeadlessRaidController : MonoBehaviour
{
    public HeadlessRaidController(IntPtr pointer) : base(pointer)
    {
    }

    public void FixedUpdate()
    {
        Physics.SyncTransforms();
    }
}
