import XCTest
@testable import PS4Mem

/// JSON as `GET /api/processes/85/maps` writes it: an unnamed heap (selected), a named "anon" map
/// (selected), executable code (read-only, not selected).
func sectionsJSON(filter: [String] = ["anon", "exe", "Game", "dlmalloc", "SmallBlockAllocator"],
                  codeSelected: Bool = false) -> String {
    let filterJSON = filter.map { "\"\($0)\"" }.joined(separator: ", ")
    return """
    {
      "pid": 85,
      "nameFilter": [\(filterJSON)],
      "includeReadOnly": false,
      "sections": [
        {"name": "ac3_engine_orbis_fn.elf", "displayName": "ac3_engine_orbis_fn.elf", "start": "0x400000", "end": "0x1400000",
         "size": 16777216, "offset": "0x0", "prot": 5, "protText": "r-x", "selected": \(codeSelected), "reason": "not writable"},
        {"name": "anon:00000001", "displayName": "anon:00000001", "start": "0x200000000", "end": "0x200100000",
         "size": 1048576, "offset": "0x0", "prot": 3, "protText": "rw-", "selected": true, "reason": "name contains 'anon'"},
        {"name": "", "displayName": "(unnamed)", "start": "0x8000000000", "end": "0x80B9000000",
         "size": 3103784960, "offset": "0x0", "prot": 3, "protText": "rw-", "selected": true, "reason": "unnamed (anonymous memory)"}
      ],
      "selectedCount": \(codeSelected ? 3 : 2),
      "totalCount": 3,
      "selectedBytes": 3104833536,
      "totalBytes": 3121610752,
      "skippedMaps": 4
    }
    """
}

@MainActor
final class SectionsModelTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    /// Answers every maps call with the given JSON.
    private func serve(_ body: String) {
        StubURLProtocol.handler = { _ in .init(status: 200, body: body) }
    }

    private var lastURL: URL? { StubURLProtocol.requests.last?.url }

    private func query(_ url: URL?) -> [String: String] {
        let items = url.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems } ?? []
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
    }

    func testLoadUsesTheDefaultFilterAndTheBackendsSelection() async {
        serve(sectionsJSON())
        let model = SectionsModel(client: stubClient())
        await model.load(pid: 85)

        XCTAssertEqual(lastURL?.path, "/api/processes/85/maps")
        XCTAssertEqual(query(lastURL), [:], "the first load sends no filter, so the backend uses its defaults")
        XCTAssertEqual(model.sections.count, 3)
        XCTAssertEqual(model.sections[2].displayName, "(unnamed)")
        XCTAssertEqual(model.selectedStarts, ["0x200000000", "0x8000000000"])
        XCTAssertEqual(model.filterText, "anon, exe, Game, dlmalloc, SmallBlockAllocator", "defaults pre-filled")
        XCTAssertTrue(model.filterIsDefault)
        XCTAssertEqual(model.skippedMaps, 4)
        XCTAssertEqual(model.selectedCount, 2)
        XCTAssertEqual(model.totalCount, 3)
        XCTAssertEqual(model.selectedBytes, 1_048_576 + 3_103_784_960)
        XCTAssertTrue(model.countText.hasPrefix("2 / 3 selected · "), model.countText)
    }

    func testTicksSelectAllAndNone() async {
        serve(sectionsJSON())
        let model = SectionsModel(client: stubClient())
        await model.load(pid: 85)

        let code = model.sections[0]
        XCTAssertFalse(model.isSelected(code))
        model.setSelected(code, true)
        XCTAssertTrue(model.isSelected(code), "any section can be ticked, also one the filter left out")
        XCTAssertEqual(model.selectedCount, 3)
        model.setSelected(model.sections[2], false)
        XCTAssertEqual(model.selectedStarts, ["0x400000", "0x200000000"], "list order")

        model.selectNone()
        XCTAssertEqual(model.selectedCount, 0)
        model.selectAll()
        XCTAssertEqual(model.selectedCount, 3)
        XCTAssertEqual(model.selectedBytes, model.totalBytes)
        XCTAssertEqual(StubURLProtocol.requests.count, 1, "ticking is local; nothing is sent")
    }

    func testApplyFilterSendsTheFieldAndReplacesTheTicks() async {
        serve(sectionsJSON())
        let model = SectionsModel(client: stubClient())
        await model.load(pid: 85)
        model.selectNone()

        serve(sectionsJSON(filter: ["anon", "elf"], codeSelected: true))
        model.filterText = " anon ; elf, "
        model.includeReadOnly = true
        await model.applyFilter()

        XCTAssertEqual(query(lastURL), ["filter": "anon,elf", "includeReadOnly": "true"])
        XCTAssertEqual(model.selectedCount, 3, "the backend's selection for the new filter")
        XCTAssertEqual(model.filterText, " anon ; elf, ", "the field is left as typed")
        XCTAssertFalse(model.filterIsDefault)
    }

    func testAnEmptyFilterMeansAnyName() async {
        serve(sectionsJSON())
        let model = SectionsModel(client: stubClient())
        await model.load(pid: 85)
        model.filterText = "  "
        await model.applyFilter()
        XCTAssertEqual(query(lastURL), ["filter": ","], "',' is an empty list to the backend, not 'use the defaults'")
    }

    func testResetPutsTheDefaultsBack() async {
        serve(sectionsJSON())
        let model = SectionsModel(client: stubClient())
        await model.load(pid: 85)
        model.filterText = "Game"
        model.includeReadOnly = true
        await model.resetFilter()
        XCTAssertEqual(model.filterText, "anon, exe, Game, dlmalloc, SmallBlockAllocator")
        XCTAssertFalse(model.includeReadOnly)
        XCTAssertEqual(query(lastURL), ["filter": "anon,exe,Game,dlmalloc,SmallBlockAllocator"])
    }

    func testErrorIsShownAndTheOldListKept() async {
        serve(sectionsJSON())
        let model = SectionsModel(client: stubClient())
        await model.load(pid: 85)
        StubURLProtocol.handler = { _ in .init(status: 502, body: #"{"error": "ps4debug didn't answer."}"#) }
        await model.applyFilter()
        XCTAssertEqual(model.error, "ps4debug didn't answer.")
        XCTAssertEqual(model.sections.count, 3)
    }

    func testClearForgetsEverything() async {
        serve(sectionsJSON())
        let model = SectionsModel(client: stubClient())
        await model.load(pid: 85)
        model.clear()
        XCTAssertNil(model.pid)
        XCTAssertTrue(model.sections.isEmpty)
        XCTAssertEqual(model.selectedCount, 0)
        XCTAssertEqual(model.filterText, "")
    }

    func testParseFilter() {
        XCTAssertEqual(SectionsModel.parseFilter("anon, exe;Game,,  "), ["anon", "exe", "Game"])
        XCTAssertEqual(SectionsModel.parseFilter(""), [])
    }
}
