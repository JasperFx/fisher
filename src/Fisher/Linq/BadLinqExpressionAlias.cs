// fisher#530: Fisher throws JasperFx.BadLinqExpressionException directly. The Fisher.Linq subclass
// that used to stand here is deleted, because an exception that means the same thing on Marten,
// Polecat and Fisher is one type in JasperFx, thrown by all three. The alias keeps the many refusal
// sites in the LINQ provider reading as they did without a `using JasperFx;` in each.
global using BadLinqExpressionException = JasperFx.BadLinqExpressionException;
