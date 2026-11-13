using System.Collections;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace Fika.Core;
internal static class Il2CppCoroutineExtensions
{
    public static Coroutine StartCoroutine(this MonoBehaviour host, IEnumerator routine)
    {
        return host.StartCoroutine(routine.WrapToIl2Cpp());
    }
}
