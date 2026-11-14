using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

[DbContext(typeof(FinanceFlowDbContext))]
public sealed class FinanceFlowDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => AccountsDeliveryAModel.Build(modelBuilder);
}
