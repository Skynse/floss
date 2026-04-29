#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct CanvasPoint {
    pub x: f32,
    pub y: f32,
}

impl CanvasPoint {
    pub fn distance_to(self, other: CanvasPoint) -> f32 {
        let dx = other.x - self.x;
        let dy = other.y - self.y;
        (dx * dx + dy * dy).sqrt()
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct CanvasRect {
    pub left: f32,
    pub top: f32,
    pub right: f32,
    pub bottom: f32,
}

impl CanvasRect {
    pub fn empty() -> Self {
        Self::default()
    }

    pub fn from_point(point: CanvasPoint) -> Self {
        Self {
            left: point.x,
            top: point.y,
            right: point.x,
            bottom: point.y,
        }
    }

    pub fn include_point(&mut self, point: CanvasPoint) {
        self.left = self.left.min(point.x);
        self.top = self.top.min(point.y);
        self.right = self.right.max(point.x);
        self.bottom = self.bottom.max(point.y);
    }

    pub fn inflate(self, amount: f32) -> Self {
        Self {
            left: self.left - amount,
            top: self.top - amount,
            right: self.right + amount,
            bottom: self.bottom + amount,
        }
    }

    pub fn is_empty(self) -> bool {
        self.right <= self.left || self.bottom <= self.top
    }
}
