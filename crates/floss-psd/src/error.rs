use thiserror::Error;

#[derive(Error, Debug)]
pub enum PsdError {
    #[error("PSD parse error: {0}")]
    Parse(#[from] psd::PsdError),

    #[error("Layer '{0}' has no pixel data")]
    MissingPixelData(String),
}

pub type Result<T> = std::result::Result<T, PsdError>;
