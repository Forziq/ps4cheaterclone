import Foundation

// The scanner's pickers. The names sent to the backend match ScanNames in the C# backend
// (src/PS4Mem.Core/Scanning/ScanValueType.cs and ScanCompare.cs, ShortName()).

/// What to scan for. Integers also have a signed/unsigned toggle, so "4 bytes" is u32 or i32.
enum ValueKind: String, CaseIterable, Identifiable {
    case byte1, byte2, byte4, byte8, float, double, string, bytes

    var id: String { rawValue }

    var label: String {
        switch self {
        case .byte1: return "1 byte"
        case .byte2: return "2 bytes"
        case .byte4: return "4 bytes"
        case .byte8: return "8 bytes"
        case .float: return "Float"
        case .double: return "Double"
        case .string: return "String"
        case .bytes: return "Byte array"
        }
    }

    var isInteger: Bool { [.byte1, .byte2, .byte4, .byte8].contains(self) }
    var isNumber: Bool { isInteger || self == .float || self == .double }

    /// The backend's type name: "u32", "i8", "f32", "bytes" ...
    func typeName(signed: Bool) -> String {
        let sign = signed ? "i" : "u"
        switch self {
        case .byte1: return sign + "8"
        case .byte2: return sign + "16"
        case .byte4: return sign + "32"
        case .byte8: return sign + "64"
        case .float: return "f32"
        case .double: return "f64"
        case .string: return "string"
        case .bytes: return "bytes"
        }
    }

    /// The other way round, for results the backend already has: "i16" → (2 bytes, signed).
    static func from(typeName: String) -> (kind: ValueKind, signed: Bool)? {
        for kind in allCases {
            if kind.typeName(signed: false) == typeName { return (kind, false) }
            if kind.typeName(signed: true) == typeName { return (kind, true) }
        }
        return nil
    }
}

/// How to compare. First scans only use the ones that don't need a previous value; next scans
/// use all but "unknown initial value".
enum CompareKind: String, CaseIterable, Identifiable {
    case exact = "exact"
    case biggerThan = "bigger than"
    case smallerThan = "smaller than"
    case between = "between"
    case increased = "increased"
    case increasedBy = "increased by"
    case decreased = "decreased"
    case decreasedBy = "decreased by"
    case changed = "changed"
    case unchanged = "unchanged"
    case unknownInitial = "unknown initial value"

    var id: String { rawValue }

    /// The name the backend takes.
    var apiName: String { rawValue }

    var label: String {
        switch self {
        case .exact: return "Exact value"
        case .biggerThan: return "Bigger than"
        case .smallerThan: return "Smaller than"
        case .between: return "Between"
        case .increased: return "Increased"
        case .increasedBy: return "Increased by"
        case .decreased: return "Decreased"
        case .decreasedBy: return "Decreased by"
        case .changed: return "Changed"
        case .unchanged: return "Unchanged"
        case .unknownInitial: return "Unknown initial value"
        }
    }

    /// How many value fields to show: 0, 1 or 2 (between).
    var valueCount: Int {
        switch self {
        case .between: return 2
        case .increased, .decreased, .changed, .unchanged, .unknownInitial: return 0
        default: return 1
        }
    }

    /// Compares against the previous scan's value, so only a next scan can use it.
    var needsPrevious: Bool {
        [.increased, .increasedBy, .decreased, .decreasedBy, .changed, .unchanged].contains(self)
    }

    var allowedOnFirstScan: Bool { !needsPrevious }
    var allowedOnNextScan: Bool { self != .unknownInitial }

    /// Strings and byte arrays have no order: only exact, changed and unchanged.
    func allowed(for kind: ValueKind) -> Bool {
        kind.isNumber || [.exact, .changed, .unchanged].contains(self)
    }

    /// The compares to offer for a first or next scan of this kind.
    static func available(for kind: ValueKind, nextScan: Bool) -> [CompareKind] {
        allCases.filter { ($0.allowed(for: kind)) && (nextScan ? $0.allowedOnNextScan : $0.allowedOnFirstScan) }
    }

    /// Placeholder text for the value field(s).
    var valuePrompts: [String] {
        switch self {
        case .between: return ["From", "To"]
        case .biggerThan: return ["More than"]
        case .smallerThan: return ["Less than"]
        case .increasedBy, .decreasedBy: return ["By"]
        default: return valueCount == 1 ? ["Value"] : []
        }
    }
}
