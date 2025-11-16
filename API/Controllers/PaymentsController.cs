using API.Extensions;
using API.SignalR;
using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;
using Core.Specifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Stripe;

namespace API.Controllers;

public class PaymentsController(
    IPaymentService paymentService,
    IUnitOfWork unitOfWork,
    ILogger<PaymentsController> logger,
    IConfiguration config,
    IHubContext<NotificationHub> hubContext) : BaseApiController
{
    private readonly string _whSecret = config["StipeSettings:WhSecret"]!;
    
    [Authorize]
    [HttpPost("{cartId}")]
    public async Task<ActionResult<ShoppingCart>> CreateUpdatePaymentIntent(string cartId)
    {
        var cart = await paymentService.CreateOrUpdatePaymentIntent(cartId);

        if (cart == null) return BadRequest("Problem with your cart");

        return Ok(cart);
    }

    [HttpGet("delivery-methods")]
    public async Task<ActionResult<IReadOnlyList<DeliveryMethod>>> GetDeliveryMethods()
    {
        return Ok(await unitOfWork.Repository<DeliveryMethod>().ListAllAsync());
    }
    
    [HttpPost("webhook")]
    public async Task<IActionResult> StripeWebhook()
    {
        // Enable buffering so we can read the body multiple times
        Request.EnableBuffering();
    
        var json = await new StreamReader(Request.Body).ReadToEndAsync();
    
        // Reset the stream position so Stripe can read it again for signature verification
        Request.Body.Position = 0;

        try
        {
            var stripeEvent = ConstructStripeEvent(json);

            if (stripeEvent.Data.Object is not PaymentIntent intent)
            {
                return BadRequest("Invalid event data");
            }

            await HandlePaymentIntentSucceeded(intent);

            return Ok();
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe webhook error");
            return StatusCode(StatusCodes.Status500InternalServerError, "Stripe webhook error");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred");
            return StatusCode(StatusCodes.Status500InternalServerError, "An unexpected error occurred");
        }
    }

    
    private async Task HandlePaymentIntentSucceeded(PaymentIntent intent)
    {
        logger.LogInformation($"Processing payment intent: {intent.Id}, Status: {intent.Status}");

        if (intent.Status == "succeeded")
        {
            Order? order = null;
            int maxRetries = 3;
            int retryDelayMs = 1000;

            for (int i = 0; i < maxRetries; i++)
            {
                var spec = new OrderSpecification(intent.Id, true);
                order = await unitOfWork.Repository<Order>().GetEntityWithSpec(spec);

                if (order != null)
                {
                    break;
                }

                if (i < maxRetries - 1)
                {
                    logger.LogWarning($"Order not found for payment intent: {intent.Id}. Retry {i + 1}/{maxRetries}");
                    await Task.Delay(retryDelayMs);
                }
            }

            if (order == null)
            {
                logger.LogError($"Order not found for payment intent: {intent.Id} after {maxRetries} retries");
                throw new Exception("Order not found");
            }

            logger.LogInformation($"Found order: {order.Id}, Total: {order.GetTotal()}");

            var orderTotalInCents = (long)Math.Round(order.GetTotal() * 100,
                MidpointRounding.AwayFromZero);

            if (orderTotalInCents != intent.Amount)
            {
                logger.LogWarning(
                    $"Payment mismatch - Order total: {order.GetTotal()}, Intent amount: {intent.Amount}");
                order.Status = OrderStatus.PaymentMismatch;
            }
            else
            {
                logger.LogInformation($"Payment successful for order: {order.Id}");
                order.Status = OrderStatus.PaymentReceived;
            }

            await unitOfWork.Complete();
            logger.LogInformation($"Order status updated successfully");

            var connectionId = NotificationHub.GetConnectionIdByEmail(order.BuyerEmail);

            if (!string.IsNullOrEmpty(connectionId))
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync("OrderCompleteNotification", order.ToDto());
            }
        }
    }

    private Event ConstructStripeEvent(string json)
    {
        try
        {
            var stuff =  EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], _whSecret);
            return stuff;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to construct stripe event");
            throw new StripeException("Invalid signature");
        }
    }
}