# llmed
Small mediator patterned library for orchestration, developing alongside other LLM experiments, so that's why the name is LLMed...

`llmed` is a standalone C# orchestration library with:

- request/handler mediation (`IRequest<TResponse>`, `IRequestHandler<TRequest, TResponse>`)
- ASP.NET-style pipeline behaviors (`IPipelineBehavior<TRequest, TResponse>`)
- in-process event publishing (`IEvent`, `IEventHandler<TEvent>`)

Use `OrchestratorMediator` with your own resolver functions (or your preferred DI container) to keep outer infrastructure concerns separate from inner application logic.
