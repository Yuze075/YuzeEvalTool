#nullable enable
using System;
using Puerts;

namespace YuzeToolkit
{
    public static class PuerTsBackendFactory
    {
        public static ScriptEnv Create(EvalScriptLoader loader)
        {
#if UNITY_WEBGL && YUZE_USE_PUER_TS_WEBGL
            return new ScriptEnv(new BackendWebGL(loader));
#elif !UNITY_WEBGL && YUZE_USE_PUER_TS_V8
            return new ScriptEnv(new BackendV8(loader));
#elif YUZE_USE_PUER_TS_QUICK_JS
            return new ScriptEnv(new BackendQuickJS(loader));
#elif !UNITY_WEBGL && YUZE_USE_PUER_TS_NODE_JS
            return new ScriptEnv(new BackendNodeJS(loader));
#else
            throw new InvalidOperationException("No supported PuerTS backend package is available. Install com.tencent.puerts.nodejs, com.tencent.puerts.v8, com.tencent.puerts.quickjs, or com.tencent.puerts.webgl.");
#endif
        }

        public static string SelectedBackendName
        {
            get
            {
#if UNITY_WEBGL && YUZE_USE_PUER_TS_WEBGL
                return "WebGL";
#elif !UNITY_WEBGL && YUZE_USE_PUER_TS_V8
                return "V8";
#elif YUZE_USE_PUER_TS_QUICK_JS
                return "QuickJS";
#elif !UNITY_WEBGL && YUZE_USE_PUER_TS_NODE_JS
                return "NodeJS";
#else
                return "None";
#endif
            }
        }
    }
}
