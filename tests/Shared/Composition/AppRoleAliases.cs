extern alias AgentApp;

// Presentation-only contracts are asserted in the App role that owns them.
// Worker services cross these boundaries through the explicit test port adapters.
global using IAgentWorkspaceEditorContributor = AgentApp::Sunder.Generated.Packages.Younics_Ai_Agent_Services.IWorkspaceEditorService;
global using AgentWorkspaceEditorContext = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentWorkspaceEditorContext;
global using AgentEditorSection = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorSection;
global using AgentEditorField = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorField;
global using AgentEditorFieldKind = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorFieldKind;
global using AgentEditorOption = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorOption;
global using AgentEditorListItem = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorListItem;
global using AgentEditorAction = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorAction;
global using AgentEditorActionKind = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorActionKind;
global using AgentEditorFieldValue = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorFieldValue;
global using AgentEditorSaveRequest = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorSaveRequest;
global using AgentEditorSaveResult = AgentApp::Sunder.Package.Agent.Contracts.Models.AgentEditorSaveResult;
global using IAgentRuntimeAvailability = AgentApp::Sunder.Package.Agent.Runtime.IAgentRuntimeAvailability;
global using AgentRuntimeConnectionState = AgentApp::Sunder.Package.Agent.Runtime.AgentRuntimeConnectionState;
