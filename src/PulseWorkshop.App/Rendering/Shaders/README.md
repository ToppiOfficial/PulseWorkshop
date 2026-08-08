# Preview shaders

`mesh.vert` / `mesh.frag` are the GLSL sources; the `.spv` files next to them are the compiled
SPIR-V the app actually loads (embedded resources - see `PulseWorkshop.App.csproj`).

The `.spv` is committed on purpose: a Vulkan *runtime* ships with every GPU driver, but a shader
*compiler* does not, and requiring the Vulkan SDK just to build the app would be a much bigger ask
than checking in 2 KB. Nothing at build or run time reads the `.glsl` sources.

To regenerate after editing a shader (throwaway project, delete it afterwards):

```
dotnet new console -o spv && cd spv
dotnet add package Vortice.ShaderCompiler
```

```csharp
using Vortice.ShaderCompiler;

string dir = @"...\src\PulseWorkshop.App\Rendering\Shaders";
using var compiler = new Compiler();
foreach (var (file, kind) in new (string, ShaderKind)[]
         { ("mesh.vert", ShaderKind.VertexShader), ("mesh.frag", ShaderKind.FragmentShader) })
{
    var options = new CompilerOptions
    {
        ShaderStage = kind,
        SourceLanguage = SourceLanguage.GLSL,
        OptimizationLevel = OptimizationLevel.Performance,
        TargetEnv = TargetEnvironmentVersion.Vulkan_1_0,
    };
    var result = compiler.Compile(File.ReadAllText(Path.Combine(dir, file)), file, options);
    if (result.Status != CompilationStatus.Success)
        throw new Exception($"{file}: {result.ErrorMessage}");
    File.WriteAllBytes(Path.Combine(dir, file + ".spv"), result.Bytecode);
}
```

The push-constant block is declared identically in both stages and must stay that way - the
renderer pushes one 80-byte blob (`mat4 mvp` + `vec4 color`) covering vertex and fragment.
