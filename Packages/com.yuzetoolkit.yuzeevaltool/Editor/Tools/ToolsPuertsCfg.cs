#nullable enable
using System;
using System.Collections.Generic;
using Puerts;

namespace YuzeToolkit
{
    [Configure]
    public sealed class ToolsPuertsCfg
    {
        [Binding]
        private static IEnumerable<Type> Bindings
        {
            get
            {
                return new List<Type>
                {
                    // Runtime
                    typeof(RuntimeTool),
                    typeof(ObjectsTool),
                    typeof(ComponentsTool),
                    typeof(DiagnosticsTool),
                    typeof(InspectTool),
                    typeof(ReflectionTool),
                    typeof(ObserveFramesTool),
                    typeof(ToolManagerTool),
                };
            }
        }
    }
}
