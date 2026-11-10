// ops/queries/relay.kql as Log Analytics saved searches, one per "// ==== <id>: <description>"
// block, so the person on call picks a query in the portal instead of pasting a copy that drifts
// from the file. The file stays the one source: adding or removing a block needs no edit here.
// tests/AgentBridge.Quality.Tests/RelayQueriesTests.cs fails CI on a block this cannot deploy.

@description('The workspace that holds the relay\'s logs.')
param workspaceName string

var source = replace(loadTextContent('../../ops/queries/relay.kql'), '\r', '')

// A marker is at the start of a line: the file's header quotes "// ==== <id>:" mid-line, and
// splitting on that would make a block of it. Element 0 is the header, before the first marker.
var blocks = skip(split('\n${source}', '\n// ==== '), 1)

var searches = map(blocks, block => {
  id: trim(substring(block, 0, indexOf(block, ':')))
  displayName: trim(substring(block, indexOf(block, ':') + 1, indexOf(block, '\n') - indexOf(block, ':') - 1))
  query: trim(substring(block, indexOf(block, '\n') + 1))
})

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource savedSearches 'Microsoft.OperationalInsights/workspaces/savedSearches@2020-08-01' = [for s in searches: {
  parent: workspace
  name: s.id
  properties: {
    category: 'Slipgates relay'
    displayName: s.displayName
    query: s.query
    version: 2
  }
}]
