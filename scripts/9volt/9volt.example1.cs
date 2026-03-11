// SPDX-License-Identifier: MPL-2.0

using System.Threading.Tasks;
using Holo.Scripting;
using Holo.Scripting.Models;
using Microsoft.Extensions.Logging;

//css_include 9volt.calculator.lib.cs;

public class Example1 : HoloScript
{
    private static readonly PackageInfo _info = new()
    {
        DisplayName = "Example Script 1",
        QualifiedName = "9volt.example1",
        Exports =
        [
            new MethodInfo
            {
                DisplayName = "Addition!",
                QualifiedName = "add",
                Submenu = "Math",
            },
            new MethodInfo
            {
                DisplayName = "Multiplication!",
                QualifiedName = "multiply",
                Submenu = "Math",
            },
        ],
        LogDisplay = LogDisplay.OnError,
    };

    /// <inheritdoc />
    public override async Task<ExecutionResult> ExecuteAsync(
        string? methodName,
        ScriptArgs? args = null
    )
    {
        if (string.IsNullOrEmpty(methodName))
        {
            Logger.LogInformation($"This is the primary execution entry point!");
            return ExecutionResult.Success;
        }

        switch (methodName)
        {
            case "add":
                Logger.LogInformation($"The answer to your question is {Calculator.Add(40, 1)}");
                break;
            case "multiply":
                Logger.LogInformation($"The answer to your question is {Calculator.Multiply(17, 2)}");
                break;
            default:
                break;
        }
        return ExecutionResult.Success;
    }

    /// <inheritdoc />
    public Example1()
        : base(_info)
    {
        Logger.LogInformation("Initialized test script!");
    }
}
