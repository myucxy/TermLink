const MAX_COLUMNS: u32 = 512;
const WORD_BITS: u32 = u64::BITS;
const WORD_COUNT: usize = (MAX_COLUMNS / WORD_BITS) as usize;

/// Fixed-capacity terminal tab stops with a separately tracked visible width.
///
/// Bits outside `columns` remain stored. This lets a shrink followed by an
/// expansion restore offscreen custom stops without recreating cleared stops.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct TabStops {
    columns: u32,
    words: [u64; WORD_COUNT],
}

impl TabStops {
    pub(crate) fn new(columns: u32) -> Self {
        assert!(
            (1..=MAX_COLUMNS).contains(&columns),
            "tab-stop width must be within 1..=512"
        );
        let mut result = Self {
            columns,
            words: [0; WORD_COUNT],
        };
        result.restore_every_eight();
        result
    }

    pub(crate) fn from_words(columns: u32, words: [u64; WORD_COUNT]) -> Option<Self> {
        if !(1..=MAX_COLUMNS).contains(&columns) {
            return None;
        }
        Some(Self { columns, words })
    }

    #[cfg(test)]
    pub(crate) const fn columns(&self) -> u32 {
        self.columns
    }

    /// Returns all 512 backing bits, including temporarily offscreen stops.
    pub(crate) const fn words(&self) -> &[u64; WORD_COUNT] {
        &self.words
    }

    /// Reads a backing bit. Offscreen columns below 512 remain addressable.
    pub(crate) fn get(&self, column: u32) -> bool {
        bit_location(column)
            .map(|(word, mask)| self.words[word] & mask != 0)
            .unwrap_or(false)
    }

    /// Writes a backing bit. Returns false when `column` exceeds the 512-bit cap.
    pub(crate) fn set(&mut self, column: u32, enabled: bool) -> bool {
        let Some((word, mask)) = bit_location(column) else {
            return false;
        };
        if enabled {
            self.words[word] |= mask;
        } else {
            self.words[word] &= !mask;
        }
        true
    }

    /// Implements HTS at a visible cursor column.
    pub(crate) fn set_current(&mut self, column: u32) -> bool {
        if column >= self.columns {
            return false;
        }
        self.set(column, true)
    }

    /// Implements `TBC 0` at a visible cursor column.
    pub(crate) fn clear_current(&mut self, column: u32) -> bool {
        if column >= self.columns {
            return false;
        }
        self.set(column, false)
    }

    /// Implements `TBC 3`, including all temporarily offscreen stops.
    pub(crate) fn clear_all(&mut self) {
        self.words.fill(0);
    }

    /// Finds the requested following stop, falling back to the last visible cell.
    /// A count of zero has the terminal command default of one.
    pub(crate) fn next(&self, column: u32, count: u32) -> u32 {
        let last = self.columns - 1;
        let mut result = column.min(last);
        for _ in 0..count.max(1).min(self.columns) {
            let Some(found) = (result + 1..self.columns).find(|&candidate| self.get(candidate))
            else {
                return last;
            };
            result = found;
        }
        result
    }

    /// Finds the requested preceding stop, falling back to the first visible cell.
    /// A count of zero has the terminal command default of one.
    pub(crate) fn previous(&self, column: u32, count: u32) -> u32 {
        let last = self.columns - 1;
        let mut result = column.min(last);
        for _ in 0..count.max(1).min(self.columns) {
            let Some(found) = (0..result).rev().find(|&candidate| self.get(candidate)) else {
                return 0;
            };
            result = found;
        }
        result
    }

    /// Changes only the visible width; backing bits are deliberately untouched.
    pub(crate) fn resize(&mut self, columns: u32) -> bool {
        if !(1..=MAX_COLUMNS).contains(&columns) {
            return false;
        }
        self.columns = columns;
        true
    }

    /// Restores the every-eight-columns defaults across the complete backing cap.
    pub(crate) fn hard_reset(&mut self) {
        self.restore_every_eight();
    }

    fn restore_every_eight(&mut self) {
        self.words.fill(0);
        for column in (8..MAX_COLUMNS).step_by(8) {
            let set = self.set(column, true);
            debug_assert!(set);
        }
    }
}

fn bit_location(column: u32) -> Option<(usize, u64)> {
    if column >= MAX_COLUMNS {
        return None;
    }
    let word = usize::try_from(column / WORD_BITS).expect("bounded word index");
    let bit = column % WORD_BITS;
    Some((word, 1_u64 << bit))
}

#[cfg(test)]
mod tests {
    use super::{MAX_COLUMNS, TabStops, WORD_COUNT};

    #[test]
    fn defaults_cover_every_eighth_column_but_not_column_zero() {
        let tabs = TabStops::new(10);
        for column in 0..MAX_COLUMNS {
            assert_eq!(tabs.get(column), column != 0 && column % 8 == 0);
        }
        assert!(!tabs.get(MAX_COLUMNS));
        assert_eq!(tabs.words().len(), WORD_COUNT);
        assert_eq!(tabs.words()[0] & 1, 0);
    }

    #[test]
    fn get_and_set_cover_both_cap_boundaries() {
        let mut tabs = TabStops::new(MAX_COLUMNS);
        assert!(tabs.set(0, true));
        assert!(tabs.get(0));
        assert!(tabs.set(MAX_COLUMNS - 1, true));
        assert!(tabs.get(MAX_COLUMNS - 1));
        assert!(tabs.set(MAX_COLUMNS - 1, false));
        assert!(!tabs.get(MAX_COLUMNS - 1));

        let words = tabs.words().to_owned();
        assert!(!tabs.set(MAX_COLUMNS, true));
        assert!(!tabs.get(MAX_COLUMNS));
        assert_eq!(tabs.words(), &words);
    }

    #[test]
    fn hts_and_tbc_current_require_a_visible_column() {
        let mut tabs = TabStops::new(12);
        assert!(tabs.set_current(3));
        assert!(tabs.get(3));
        assert!(tabs.clear_current(3));
        assert!(!tabs.get(3));
        assert!(!tabs.set_current(12));
        assert!(!tabs.clear_current(12));
    }

    #[test]
    fn next_and_previous_support_counts_and_edge_fallbacks() {
        let tabs = TabStops::new(24);
        assert_eq!(tabs.next(0, 0), 8);
        assert_eq!(tabs.next(0, 1), 8);
        assert_eq!(tabs.next(0, 2), 16);
        assert_eq!(tabs.next(0, 3), 23);
        assert_eq!(tabs.next(8, 1), 16);
        assert_eq!(tabs.next(23, u32::MAX), 23);

        assert_eq!(tabs.previous(20, 0), 16);
        assert_eq!(tabs.previous(20, 1), 16);
        assert_eq!(tabs.previous(20, 2), 8);
        assert_eq!(tabs.previous(20, 3), 0);
        assert_eq!(tabs.previous(0, u32::MAX), 0);
    }

    #[test]
    fn shrink_and_expand_preserve_hidden_default_and_custom_stops() {
        let mut tabs = TabStops::new(24);
        tabs.clear_all();
        assert!(tabs.set_current(15));
        assert!(tabs.set_current(20));
        assert!(tabs.resize(10));
        assert_eq!(tabs.columns(), 10);
        assert!(tabs.get(15));
        assert!(tabs.get(20));
        assert_eq!(tabs.next(0, 1), 9);

        assert!(tabs.resize(24));
        assert_eq!(tabs.next(0, 1), 15);
        assert_eq!(tabs.next(15, 1), 20);
    }

    #[test]
    fn expansion_does_not_recreate_stops_cleared_by_the_user() {
        let mut tabs = TabStops::new(24);
        assert!(tabs.clear_current(16));
        assert!(tabs.resize(10));
        assert!(tabs.resize(24));
        assert!(!tabs.get(16));

        tabs.clear_all();
        assert!(tabs.resize(MAX_COLUMNS));
        assert!(tabs.words().iter().all(|word| *word == 0));
        assert_eq!(tabs.next(0, 1), MAX_COLUMNS - 1);
    }

    #[test]
    fn invalid_resize_leaves_state_unchanged() {
        let mut tabs = TabStops::new(80);
        let original = tabs.clone();
        assert!(!tabs.resize(0));
        assert!(!tabs.resize(MAX_COLUMNS + 1));
        assert_eq!(tabs, original);
    }

    #[test]
    fn hard_reset_restores_all_defaults_without_changing_width() {
        let mut tabs = TabStops::new(17);
        tabs.clear_all();
        assert!(tabs.set_current(3));
        tabs.hard_reset();

        assert_eq!(tabs.columns(), 17);
        for column in 0..MAX_COLUMNS {
            assert_eq!(tabs.get(column), column != 0 && column % 8 == 0);
        }
    }

    #[test]
    #[should_panic(expected = "tab-stop width must be within 1..=512")]
    fn construction_rejects_zero_width() {
        let _ = TabStops::new(0);
    }

    #[test]
    #[should_panic(expected = "tab-stop width must be within 1..=512")]
    fn construction_rejects_width_above_cap() {
        let _ = TabStops::new(MAX_COLUMNS + 1);
    }
}
