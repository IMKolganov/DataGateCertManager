//! Wire format shared with DataGateOpenVpnManager UDP proxy:
//! one WebSocket binary message may contain one or more frames
//! `[u16_be len][payload]` with payload length 1..=65535.

pub const MAX_PAYLOAD: usize = 65535;
pub const MAX_FRAME: usize = 2 + MAX_PAYLOAD;
pub const BATCH_CAPACITY: usize = MAX_FRAME;
pub const BATCH_TARGET: usize = 48 * 1024;

#[inline]
pub fn write_frame(dest: &mut [u8], payload: &[u8]) -> Option<usize> {
    let n = payload.len();
    if n == 0 || n > MAX_PAYLOAD || dest.len() < 2 + n {
        return None;
    }
    dest[0] = (n >> 8) as u8;
    dest[1] = (n & 0xff) as u8;
    dest[2..2 + n].copy_from_slice(payload);
    Some(2 + n)
}

/// Returns `(next_offset, payload_slice)` or `None` on truncated / zero / OOB length
/// (matches historical .NET proxy stop-on-invalid behaviour).
#[inline]
pub fn try_parse_next(data: &[u8], offset: usize) -> Option<(usize, &[u8])> {
    if offset + 2 > data.len() {
        return None;
    }
    let len = ((data[offset] as usize) << 8) | (data[offset + 1] as usize);
    let start = offset + 2;
    if len == 0 || start + len > data.len() {
        return None;
    }
    Some((start + len, &data[start..start + len]))
}

#[inline]
pub fn can_append(offset: usize, payload_len: usize, capacity: usize, target: usize) -> bool {
    payload_len > 0
        && payload_len <= MAX_PAYLOAD
        && offset + 2 + payload_len <= capacity
        && (offset == 0 || offset < target)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn write_frame_big_endian_length() {
        let payload = vec![0xABu8; 1400];
        let mut dest = vec![0u8; 2 + payload.len()];
        assert_eq!(write_frame(&mut dest, &payload), Some(1402));
        assert_eq!(dest[0], 0x05);
        assert_eq!(dest[1], 0x78);
        assert_eq!(dest[2], 0xAB);
    }

    #[test]
    fn pack_and_parse_multiple_frames() {
        let mut dest = vec![0u8; 64];
        let mut off = 0;
        off += write_frame(&mut dest[off..], &[1, 2]).unwrap();
        off += write_frame(&mut dest[off..], &[3]).unwrap();
        off += write_frame(&mut dest[off..], &[4, 5, 6]).unwrap();

        let mut parsed = Vec::new();
        let mut cur = 0;
        while cur < off {
            let (next, frame) = try_parse_next(&dest[..off], cur).unwrap();
            parsed.push(frame.to_vec());
            cur = next;
        }
        assert_eq!(parsed, vec![vec![1, 2], vec![3], vec![4, 5, 6]]);
    }

    #[test]
    fn parse_stops_on_truncated_prefix() {
        assert!(try_parse_next(&[0x00], 0).is_none());
    }

    #[test]
    fn parse_stops_on_length_beyond_payload() {
        assert!(try_parse_next(&[0x00, 0x05, 1, 2, 3], 0).is_none());
    }

    #[test]
    fn parse_treats_zero_length_as_invalid() {
        assert!(try_parse_next(&[0x00, 0x00, 0x00, 0x01, 0xFF], 0).is_none());
    }

    #[test]
    fn parse_max_payload_65535() {
        let mut payload = vec![0u8; MAX_PAYLOAD];
        payload[MAX_PAYLOAD - 1] = 0x7E;
        let mut data = vec![0u8; MAX_FRAME];
        data[0] = 0xFF;
        data[1] = 0xFF;
        data[2..].copy_from_slice(&payload);
        let (next, frame) = try_parse_next(&data, 0).unwrap();
        assert_eq!(next, MAX_FRAME);
        assert_eq!(frame.len(), MAX_PAYLOAD);
        assert_eq!(frame[MAX_PAYLOAD - 1], 0x7E);
    }

    #[test]
    fn empty_batch_fits_max_payload() {
        assert!(BATCH_CAPACITY >= MAX_FRAME);
        assert!(can_append(0, MAX_PAYLOAD, BATCH_CAPACITY, BATCH_TARGET));
        let mut dest = vec![0u8; BATCH_CAPACITY];
        let payload = vec![0x42u8; MAX_PAYLOAD];
        assert_eq!(write_frame(&mut dest, &payload), Some(MAX_FRAME));
        assert_eq!(dest[MAX_FRAME - 1], 0x42);
    }

    #[test]
    fn can_append_respects_target() {
        assert!(can_append(BATCH_TARGET - 1, 100, BATCH_CAPACITY, BATCH_TARGET));
        assert!(!can_append(BATCH_TARGET, 100, BATCH_CAPACITY, BATCH_TARGET));
    }

    /// Microbench: CPU framing only (not a network proxy measurement).
    #[test]
    fn framing_cpu_throughput_smoke() {
        use std::time::{Duration, Instant};

        let payload = vec![0x5Au8; 1200];
        let mut dest = vec![0u8; BATCH_CAPACITY];
        let warmup = Instant::now();
        while warmup.elapsed() < Duration::from_millis(50) {
            let _ = write_frame(&mut dest, &payload);
        }

        let mut bytes = 0u64;
        let t0 = Instant::now();
        let window = Duration::from_millis(300);
        while t0.elapsed() < window {
            for _ in 0..64 {
                let written = write_frame(&mut dest, &payload).unwrap();
                let mut off = 0;
                while let Some((next, frame)) = try_parse_next(&dest[..written], off) {
                    bytes += frame.len() as u64;
                    off = next;
                }
            }
        }
        let secs = t0.elapsed().as_secs_f64().max(1e-9);
        let mbps = (bytes as f64) * 8.0 / secs / 1_000_000.0;
        eprintln!("framing CPU throughput: {mbps:.0} Mbps ({bytes} bytes in {secs:.3}s)");
        // Pure memory path should be multi-Gbps on any modern CPU.
        assert!(
            mbps > 2_000.0,
            "framing too slow ({mbps:.0} Mbps) — unexpected regression"
        );
    }
}
