fn main() {
    let out = "../../src/Floss.App/Native/FlossCompositeNativeMethods.g.cs";
    csbindgen::Builder::default()
        .input_extern_file("src/ffi.rs")
        .csharp_dll_name("floss_composite")
        .csharp_namespace("Floss.App.Native")
        .csharp_class_name("FlossCompositeNativeMethods")
        .csharp_class_accessibility("internal")
        .csharp_generate_const_filter(|name| name == "FLOSS_COMPOSITE_VERSION")
        .generate_csharp_file(out)
        .expect("csbindgen failed to write C# bindings");
    println!("cargo:rerun-if-changed=src/ffi.rs");
    println!("cargo:rerun-if-changed=build.rs");
}
