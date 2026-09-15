// JasperFx 2.71.0 lifts BadLinqExpressionException into the JasperFx namespace (jasperfx#795) and
// Fisher.Linq.BadLinqExpressionException now derives from it. Both names are therefore in scope in
// every test file that imports both namespaces, which is CS0104 — an ambiguity the compiler refuses
// rather than resolves, in about a hundred places.
//
// Settled once here, and settled towards FISHER'S type deliberately. These tests pin Fisher's own
// refusals, and Shouldly's Should.Throw<T> matches on the exact type rather than on assignability,
// so aliasing to the shared base would change what every one of them asserts. A test that wants the
// store-agnostic contract should name JasperFx.BadLinqExpressionException in full and say so.
global using BadLinqExpressionException = Fisher.Linq.BadLinqExpressionException;
