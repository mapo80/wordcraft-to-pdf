//! Give libwcpdf.so a SONAME on Linux, so programs linking it record a stable name.
fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").is_ok_and(|os| os == "linux") {
        println!("cargo::rustc-cdylib-link-arg=-Wl,-soname,libwcpdf.so");
    }
}
