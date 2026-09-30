using System.Runtime.CompilerServices;

// The wire shape, the question rendering and the circuit breaker are internal because they are not
// part of what a caller composes with: the port implementation and the options are. They are still
// the parts most worth pinning with a test, since a change to any of them changes what the provider
// is asked and therefore changes an answer. Opening them to this one test assembly keeps the public
// surface honest without leaving the pieces that matter untested.
[assembly: InternalsVisibleTo("StyloMail.Nimble.Tests")]
