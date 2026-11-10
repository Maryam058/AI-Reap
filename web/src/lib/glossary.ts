/**
 * Plain-language explanations of the technical terms AI-REAP uses. The terms themselves are kept
 * as they are everywhere else in the app; these only explain them to non-technical users.
 */
export interface GlossaryEntry {
  term: string;
  definition: string;
}

export const GLOSSARY: Record<string, GlossaryEntry> = {
  srs: { term: 'SRS', definition: 'Software Requirements Specification: a document describing what the system must do. Upload it as a PDF, Word (.docx) or text file.' },
  requirementSource: { term: 'Requirement source', definition: 'Any input you add for AI-REAP to analyse: an uploaded document or pasted notes. The original text is kept as-is and never overwritten.' },
  stakeholder: { term: 'Stakeholder', definition: 'A person or group affected by the project or who decides on it. Recording them does not give them access to AI-REAP.' },
  fr: { term: 'Functional requirement', definition: 'Something the system must do — for example “A patient can book an appointment online”.' },
  nfr: { term: 'Non-functional requirement', definition: 'How well the system must work — for example speed, security or availability, such as “Pages load in under 2 seconds”.' },
  businessRule: { term: 'Business rule', definition: 'A policy or constraint the business follows — for example “Appointments can be cancelled up to 24 hours before”.' },
  userStory: { term: 'User story', definition: 'A requirement written from a user’s point of view: “As a …, I want …, so that …”.' },
  acceptanceCriteria: { term: 'Acceptance criteria', definition: 'The conditions a user story must meet to count as done. They are what testers check against.' },
  clarification: { term: 'Clarification question', definition: 'A question AI raises when your input is missing information, is ambiguous or contradicts itself. Answering it makes the requirements clearer.' },
  conflict: { term: 'Conflict', definition: 'Two requirements that cannot both be true as written. One of them usually needs to change.' },
  designArtifact: { term: 'Solution design', definition: 'A description of how the system will be built: its main parts and how they work together.' },
  dataEntity: { term: 'Data entity', definition: 'A kind of information the system stores — for example Patient or Appointment — with its fields.' },
  apiSpec: { term: 'API specification', definition: 'A description of how other software talks to the system: the requests it accepts and what it returns.' },
  task: { term: 'Implementation task', definition: 'A piece of development work, linked to the requirement it delivers.' },
  testCase: { term: 'Test case', definition: 'Steps and an expected result used to check that a requirement works. Each run is recorded as passed or failed.' },
  coverage: { term: 'Test coverage', definition: 'The share of functional requirements that have at least one test case.' },
  traceability: { term: 'Traceability', definition: 'The links from each requirement to its design, tasks and tests. It shows whether anything has been missed.' },
  agentPipeline: { term: 'Agent pipeline', definition: 'The sequence of AI agents that generate the project’s artifacts. You can run them and review what they produced.' },
  lifecycleStatus: { term: 'Lifecycle status', definition: 'The project’s formal position (Draft → Completed). It only moves when the stage’s exit checks pass, and some moves need a Reviewer’s approval.' },
  reviewerApproval: { term: 'Reviewer approval', definition: 'Leaving Design and Testing needs a Reviewer — someone other than the person who asked — to approve the move.' },
  exitCheck: { term: 'Exit check', definition: 'A condition that must be met before the project can move to its next lifecycle status, for example “all requirements approved”.' },
  artifact: { term: 'Artifact', definition: 'Anything produced for the project — a requirement, a user story, a design, a task or a test case.' },
};

export function glossary(key: string): GlossaryEntry | undefined {
  return GLOSSARY[key];
}
