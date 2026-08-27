#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Puerts;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    /// <summary>
    /// PuerTS generation configuration for the Unity MCP eval environment.
    /// Use Tools > PuerTS > Generate C# Static Wrappers for bindings, or Generate index.d.ts for typings.
    /// </summary>
    [Configure]
    public sealed class PuertsCfg
    {
        [Binding]
        private static IEnumerable<Type> Bindings
        {
            get
            {
                return new List<Type>
                {
                    typeof(EvalToolRegistry),
                    typeof(EvalValueFormatter),
                    typeof(Resources),
                    typeof(TextAsset),
                    typeof(Debug),
                    typeof(Application),
                    typeof(Time),
                    typeof(Screen),
                    typeof(Mathf),
                    typeof(System.Array),
                    typeof(GameObject),
                    typeof(Component),
                    typeof(Transform),
                    typeof(Camera),
                    typeof(UnityEngine.Object),
                    typeof(Vector2),
                    typeof(Vector3),
                    typeof(Quaternion),
                    typeof(Color),
                    typeof(Action<string>),
                    typeof(Action<Action<string>>),
                };
            }
        }
    }
}
