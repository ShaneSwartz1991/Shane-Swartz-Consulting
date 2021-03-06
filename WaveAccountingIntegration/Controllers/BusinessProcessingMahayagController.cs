﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using System.Xml.Schema;
using Microsoft.Ajax.Utilities;
using Newtonsoft.Json;
using WaveAccountingIntegration.Models;


namespace WaveAccountingIntegration.Controllers
{
	public class BusinessProcessingMahayagController : BaseController
	{
		public ActionResult SmsAlertLateCustomers(int daysBetweenAlerts = 3)
		{
			var lateCustomers = GetLateCustomers();
			var messages = new ConcurrentBag<string>();

			Parallel.ForEach(lateCustomers, (customerKvp) =>
			{
				var customer = customerKvp.Key;
				var custSettings = _customerSettingsService.ExctractFromCustomerObject(customer);

				var daysSinceLastSmsAlert = (DateTime.Now - (custSettings.LastSmsAlertSent?? DateTime.Now)).Days;

				var minDaysBetweenAlerts = Math.Max(custSettings.CustomDaysBetweenSmsAlerts?? daysBetweenAlerts, daysBetweenAlerts);

				var lastPayment = customerKvp.Value.events.Where(x => x.event_type == "payment").OrderByDescending(x => x.date).FirstOrDefault();
				var lastInvoice = customerKvp.Value.events.Where(x => x.event_type == "invoice" && x.total > 0).OrderByDescending(x => x.date).First();

				var daysSinceLastPayment = (DateTime.Now - (lastPayment?.date?? DateTime.Now.AddYears(-10))).Days;

				if (
					daysSinceLastSmsAlert >= minDaysBetweenAlerts &&
					daysSinceLastPayment >= 7 &&
					lastInvoice.date <= DateTime.Today.Date.AddDays(-5) &&
					DateTime.Now.Hour > 8 &&
					custSettings.SendSmsAlerts == true
				)
				{
					//sent alert to name 1
					if (!string.IsNullOrWhiteSpace(ExtractEmailFromString(customer.address1)))
					{
						var name = customer.first_name.ToUpper().Trim();
						var body = GetLateCustomerSmsAlertBody(name, customerKvp, lastPayment, custSettings);
						           
						messages.Add($"alerting late customer:{name} on {ExtractEmailFromString(customer.address1)}");
						_sendGmail.SendSMS(ExtractEmailFromString(customer.address1), body, _appAppSettings);
					}

					//sent alert to name 2
					if (!string.IsNullOrWhiteSpace(ExtractEmailFromString(customer.address2)))
					{
						var name = customer.last_name.ToUpper().Trim();
						var body = GetLateCustomerSmsAlertBody(name, customerKvp, lastPayment, custSettings);

						messages.Add($"alerting late customer: {name} on {ExtractEmailFromString(customer.address2)}");
						_sendGmail.SendSMS(ExtractEmailFromString(customer.address2), body, _appAppSettings);
					}

					custSettings.LastSmsAlertSent = DateTime.Now;
					_customerSettingsService.SaveUpdatedCustomerSettings(customer.url, custSettings, _restService);

				}
				else
				{
					//skip alert
					messages.Add($"Skipping SmsAlert LastSmsAlertSent: {(custSettings.LastSmsAlertSent.HasValue ? custSettings.LastSmsAlertSent.Value.ToUSADateFormat() : string.Empty)}, " +
					             $"minDaysBetweenAlerts: {minDaysBetweenAlerts:00}, " +
					             $"lastInvoice.date: {lastInvoice.date.Value.ToUSADateFormat()}, " +
					             $"daysSinceLastPayment: {daysSinceLastPayment}, " +
					             $"SendSmsAlerts: {custSettings.SendSmsAlerts}, " +
					             $"for: {customerKvp.Key.name}.");
				}
			});


			ViewBag.Message = string.Join(Environment.NewLine, messages);
			return View();
		}

		private string GetLateCustomerSmsAlertBody(string name, KeyValuePair<Customer, Transaction_History> customerKvp, Event lastPayment, CustomerSettings custSettings)
		{
			var dailyRate = custSettings.SignedLeaseAgreement == true ? $"${custSettings.LateFeeDailyAmount}" : $"{custSettings.LateFeePercentRate * 100}%";
			return $"Hello {name}, " +
					$"as of today {DateTime.Today.ToUSADateFormat()} " +
					$"your balance due is ${customerKvp.Value.ending_balance} " +
					$"and your last payment of: ${lastPayment?.total} " +
					$"was received on: {lastPayment?.date.Value.ToUSADateFormat()}. " +
					$"You can see your history here: {custSettings.StatementUrl} ." +
					$"Please let me know when can you make your next payment. " +
					$"IMPORTANT NOTE: Starting March 2018 there will be: {dailyRate}; daily charge for any past due balance! ";
		}

		public ActionResult EvictionDocs(int id, string form)
		{
			var customerStatement = new Dictionary<Customer, Transaction_History>();

			var customer = _restService.Get<Customer>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/customers/{id}/").Result;
			
			var statement = _restService.Get<TransactionHistory>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/customers/{id}/statements/transaction-history/").Result;

			var trxHistory = statement.transaction_history.FirstOrDefault();

			if (trxHistory != null)
				customerStatement.Add(customer, trxHistory);

			if (form == null)
				return View(customerStatement);

			var address = _appAppSettings.MahayagAddresses.FirstOrDefault(x => x.id == customer.name.Substring(0, 4));

			List<Tennant> tenants = GetTennatsFromName(customer.name);

			ViewBag.address = address;
			ViewBag.CustomerSettings = _customerSettingsService.ExctractFromCustomerObject(customer);
			ViewBag.AppSettings = _appAppSettings;
			ViewBag.Tenants = tenants;
			var total = customerStatement.First().Value.ending_balance;
			ViewBag.invoice = customerStatement.Values.First().events.First(x=>x.invoice.invoice_amount_due == total && x.event_type == "invoice").invoice;
			ViewBag.invoice_items = _restService.Get<List<InvoiceItem>>(ViewBag.invoice.items_url).Result;
			return View(form, customerStatement);
		}

		private List<Tennant> GetTennatsFromName(string customerName)
		{
			List<Tennant> tenants = new List<Tennant>();

			int start = 0;
			int end = 0;

			do
			{
				start = customerName.IndexOf('[', start+1);
				end = customerName.IndexOf(']', end+1);

				var tenant = new Tennant()
				{
					FullName = customerName.Substring(start + 1, (end-start) - 11).ToUpper().Trim(),
					DateOfBirth = Convert.ToDateTime(customerName.Substring(end-10, 10))
				};

				var names = tenant.FullName.Split(' ').Where(x=> !string.IsNullOrWhiteSpace(x)).ToList();

				tenant.FirstName = names.First().ToUpper().Trim();
				tenant.LastName = names.Last().ToUpper().Trim();			
				tenant.MiddleName = names.Count > 2 ? names[1].ToUpper().Trim() : string.Empty ;

				tenants.Add(tenant);

			} while (customerName.IndexOf('[',end+1) > 0);

			return tenants;
		}


		public ActionResult LateCustomers()
		{
			return View(GetLateCustomers());
		}

		private Dictionary<Customer, Transaction_History> GetLateCustomers()
		{
			var allCustomers = _restService.Get<List<Customer>>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/customers/").Result;

			var activeCustomers = allCustomers.Where(x => x.active && !x.name.StartsWith("XX"));

			var allCustomerStatements = new Dictionary<Customer, Transaction_History>();

			Parallel.ForEach(activeCustomers, (customer) =>
			{
				try
				{
					var statement = _restService.Get<TransactionHistory>(
							$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/customers/{customer.id}/statements/transaction-history/")
						.Result;

					var trxHistory = statement.transaction_history.FirstOrDefault();

					if (trxHistory != null)
						allCustomerStatements.Add(customer, trxHistory);
				}
				catch
				{
					// ignored
				}
			});

			var toReturn = new Dictionary<Customer, Transaction_History>();

			foreach (var keyValuePair in allCustomerStatements.Where(x => x.Value.ending_balance > 0).OrderByDescending(x => x.Value.ending_balance))
			{
				toReturn.Add(keyValuePair.Key, keyValuePair.Value);
			}

			return toReturn;
		}

		public ActionResult RefreshBankConnections()
		{
			var refreshedSites = new ConcurrentBag<Connected_Site>();
			var Guid = _appAppSettings.PersonalGuid;

			var connectedSites = _restService.Get<List<Connected_Site>>(
				$"https://integrations.waveapps.com/{Guid}/bank/connected-sites", _headers).Result;
			if (connectedSites != null)
			{
				Parallel.ForEach(connectedSites, (site) =>
				{
					var refreshResult = _restService.Post<string, object>(
						$"https://integrations.waveapps.com/{Guid}/bank/refresh-accounts/{site.id}", null, _headers);

					if (refreshResult.Result == "Successfully started refreshing connected site")
					{
						refreshedSites.Add(site);
						Thread.Sleep(250);
					}
				});
			}

			ViewBag.Message = "RefreshBankConnections";
			return View(refreshedSites);
		}

		public ActionResult SetCustomerDefaults()
		{
			var  messages =  new ConcurrentBag<string>();

			var processedCsutomers = new List<Customer>();

			var allCustomers = _restService.Get<List<Customer>>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/customers/").Result;

			var activeCustomersToSetup = allCustomers.Where(x => x.active && !x.name.StartsWith("XX") && !x.name.StartsWith("??"));

			Parallel.ForEach(activeCustomersToSetup, (customer) =>
			{
				var custSettings = _customerSettingsService.ExctractFromCustomerObject(customer);

				var defaultCustSettings = new CustomerSettings()
				{
					ChargeLateFee = false,
					NextLateFeeChargeDate = DateTime.Today,
					LateFeePercentRate = (decimal)0.02,
					LateFeeDailyAmount = 10,
					LateFeeChargeAboveBalance = 340,
					ConsolidateInvoices = true,
					SignedLeaseAgreement = false,
					LastSmsAlertSent = DateTime.Today,
					CustomDaysBetweenSmsAlerts = 5,
					SendSmsAlerts = true,
					StatementUrl = "StatementUrl",
					EvictonNoticeDate = DateTime.Today,
					EvictionCourtCaseNumber = "0000000000000",
					EvictionCourtAssignedJudge = "JUDGEJUDGEJUDGE"
				};

				if (custSettings == null)
				{
					//add new default settings to new customer settings
					customer.shipping_details = new Shipping_details()
					{
						delivery_instructions = JsonConvert.SerializeObject(defaultCustSettings),

						ship_to_contact = $"{customer.name}",
						phone_number = String.Empty,
						address1 = String.Empty,
						address2 = String.Empty,
						city = String.Empty,
						postal_code = String.Empty,
					};

					var updatedCustomerResult =
						_restService.Patch<UpdateCustomerResult, Customer>(customer.url, customer);

					if (updatedCustomerResult.IsSuccessStatusCode == false)
					{
						throw new InvalidOperationException("SetCustomerDefaults failed");
					}

					processedCsutomers.Add(customer);
					messages.Add($"Creating new defaults for customer: {customer.name}");

				}
				else
				{
					var changesMade = false;

					if (custSettings.ChargeLateFee == null) { custSettings.ChargeLateFee = defaultCustSettings.ChargeLateFee; messages.Add($"Setting deafult value ChargeLateFee to {custSettings.ChargeLateFee} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.NextLateFeeChargeDate == null) { custSettings.NextLateFeeChargeDate = defaultCustSettings.NextLateFeeChargeDate; messages.Add($"Setting deafult value NextLateFeeChargeDate to {custSettings.NextLateFeeChargeDate} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.LateFeePercentRate == null) { custSettings.LateFeePercentRate = defaultCustSettings.LateFeePercentRate; messages.Add($"Setting deafult value LateFeePercentRate to {custSettings.LateFeePercentRate} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.LateFeeDailyAmount == null) { custSettings.LateFeeDailyAmount = defaultCustSettings.LateFeeDailyAmount; messages.Add($"Setting deafult value LateFeeDailyAmount to {custSettings.LateFeeDailyAmount} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.LateFeeChargeAboveBalance == null) { custSettings.LateFeeChargeAboveBalance = defaultCustSettings.LateFeeChargeAboveBalance; messages.Add($"Setting deafult value LateFeeChargeAboveBalance to {custSettings.LateFeeChargeAboveBalance} for customer: {customer.name}"); changesMade = true; }
					//set NextLateFeeChargeDate to today if  ChargeLateFee is false
					if (custSettings.ChargeLateFee == false && custSettings.NextLateFeeChargeDate < DateTime.Today) { custSettings.NextLateFeeChargeDate = defaultCustSettings.NextLateFeeChargeDate; messages.Add($"Setting todays date for NextLateFeeChargeDate to {custSettings.NextLateFeeChargeDate} for customer: {customer.name}"); changesMade = true; }
					
					if (custSettings.ConsolidateInvoices == null) { custSettings.ConsolidateInvoices = defaultCustSettings.ConsolidateInvoices; messages.Add($"Setting deafult value ConsolidateInvoices to {custSettings.ConsolidateInvoices} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.SignedLeaseAgreement == null) { custSettings.SignedLeaseAgreement = defaultCustSettings.SignedLeaseAgreement; messages.Add($"Setting deafult value SignedLeaseAgreement to {custSettings.SignedLeaseAgreement} for customer: {customer.name}"); changesMade = true; }

					if (custSettings.LastSmsAlertSent == null) { custSettings.LastSmsAlertSent = defaultCustSettings.LastSmsAlertSent; messages.Add($"Setting deafult value LastSmsAlertSent to {custSettings.LastSmsAlertSent} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.CustomDaysBetweenSmsAlerts == null) { custSettings.CustomDaysBetweenSmsAlerts = defaultCustSettings.CustomDaysBetweenSmsAlerts; messages.Add($"Setting deafult value CustomDaysBetweenSmsAlerts to {custSettings.CustomDaysBetweenSmsAlerts} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.SendSmsAlerts == null) { custSettings.SendSmsAlerts = defaultCustSettings.SendSmsAlerts; messages.Add($"Setting deafult value SendSmsAlerts to {custSettings.SendSmsAlerts} for customer: {customer.name}"); changesMade = true; }

					if (custSettings.StatementUrl == null) { custSettings.StatementUrl = defaultCustSettings.StatementUrl; messages.Add($"Setting deafult value StatementUrl to {custSettings.StatementUrl} for customer: {customer.name}"); changesMade = true; }

					if (custSettings.EvictonNoticeDate == null) { custSettings.EvictonNoticeDate = defaultCustSettings.EvictonNoticeDate; messages.Add($"Setting deafult value EvictonNoticeDate to {custSettings.EvictonNoticeDate} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.EvictionCourtCaseNumber == null) { custSettings.EvictionCourtCaseNumber = defaultCustSettings.EvictionCourtCaseNumber; messages.Add($"Setting deafult value EvictionCourtCaseNumber to {custSettings.EvictionCourtCaseNumber} for customer: {customer.name}"); changesMade = true; }
					if (custSettings.EvictionCourtAssignedJudge == null) { custSettings.EvictionCourtAssignedJudge = defaultCustSettings.EvictionCourtAssignedJudge; messages.Add($"Setting deafult value EvictionCourtAssignedJudge to {custSettings.EvictionCourtAssignedJudge} for customer: {customer.name}"); changesMade = true; }

					if (changesMade)
					{
						_customerSettingsService.SaveUpdatedCustomerSettings(customer.url, custSettings, _restService);
						processedCsutomers.Add(customer);
					}
					
				}
			});

			ViewBag.Message = string.Join(Environment.NewLine, messages); 
			return View(processedCsutomers);
		}

		public ActionResult ConsolidateInvoices(int narrrowByCustomerId = 0)
		{
			var processedInvoices = new List<Invoice>();

			var url =
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/invoices/?embed_customer=true";

			if (narrrowByCustomerId != 0)
				url = url + $"&customer.id={narrrowByCustomerId}";

			var invoicesDue = _restService.Get<List<Invoice>>(url).Result
				.Where(x=> x.invoice_amount_due != 0)
				.OrderBy(x=>x.customer.name)
				.ToList();

			var distinctCustomerIds = invoicesDue.Select(x => x.customer.id).Distinct();

			foreach (var customerId in distinctCustomerIds)
			{
				var customerInvoicesDue = invoicesDue.Where(x => x.customer.id == customerId).OrderByDescending(x => x.invoice_date).ToList();
				var customer = customerInvoicesDue.First().customer;
				var custSettings = _customerSettingsService.ExctractFromCustomerObject(customer);

				//consolidate more than 2 invoices when settings allow it
				if (customerInvoicesDue.Count() > 1 && custSettings.ConsolidateInvoices == true && !customerInvoicesDue.First().customer.name.StartsWith("XXX"))
				{
					var targetInvoice = customerInvoicesDue.Last();
					var sourceInvoices = customerInvoicesDue.Where(x => x.id != targetInvoice.id);

					foreach (var sourceInvoice in sourceInvoices)
					{
						//check for payments
						var payments = _restService.Get<List<Payment>>(sourceInvoice.payments_url).Result;
						if (payments.Count == 0)
						{
							//process src invoice only if there are no payments
							#region transfer Items
							var sourceItems = _restService.Get<List<InvoiceItem>>(sourceInvoice.items_url).Result;

							foreach (var sourceItem in sourceItems.Where(x=> x.quantity * x.price != 0))
							{
								var movedItem = new InvoiceItem
								{
									product = new Product { id = sourceItem.product.id },
									description = $"Transfered item: [{sourceItem.description}] " +
									              $"from invoice: {sourceInvoice.invoice_number} " +
									              $"for period from: {sourceInvoice.invoice_date.ToShortDateString()} to: {sourceInvoice.due_date.ToShortDateString()} " +
									              $"transfered on: {DateTime.Now.ToShortDateString()}",
									quantity = sourceItem.quantity,
									price = sourceItem.price
								};

								var addResult = _restService.Post<AddInvoiceItemResponse, InvoiceItem>(targetInvoice.items_url, movedItem);

								if (addResult.IsSuccessStatusCode)
								{
									
									#region zero out source item price


									var zeroSourceInvoiceItemResult = _restService.Patch<UpdateInvoiceItemResponse, InvoiceItem>(sourceItem.url, new InvoiceItem()
									{
										product = new Product() { id = sourceItem.product.id },
										description = $"[{sourceItem.description}] " +
										              $"price: {movedItem.price} " +
										              $"was moved to invoice: {targetInvoice.invoice_number} " +
										              $"on: {DateTime.Now.ToShortDateString()}",
										quantity = sourceItem.quantity,
										price = 0,
									});

									if (zeroSourceInvoiceItemResult.IsSuccessStatusCode == false)
									{
										throw new InvalidOperationException("Saving zeroSourceInvoiceItem failed");
									}

									#endregion
								}
								else
								{
									throw new InvalidOperationException("add movedItem failed");
								}
							}
							#endregion

							processedInvoices.Add(sourceInvoice);
						}
						else
						{
							//TODO: consolidate invoices with payments
						}
					}
				}
				
			}
			ViewBag.Message = "ConsolidateInvoices";
			return View(processedInvoices);
		}

		public ActionResult DisableInvoicePayments()
		{
			var invoices = _restService.Get<List<Invoice>>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/invoices/");

			var updatedInvoices = new ConcurrentBag<Invoice>();

			var messages = new ConcurrentBag<string>();

			if (invoices.IsSuccessStatusCode)
			{
				var invoicesToProcess = invoices.Result.Where(
					x => x.invoice_amount_due > 0 && 
					(x.disable_bank_payments == false || x.disable_credit_card_payments == false)
				);

				Parallel.ForEach(invoicesToProcess, (invoice) =>
				{
					var disablePaymentInvoice = new InvoiceDisablePayments
					{
						disable_credit_card_payments = true,
						disable_bank_payments = true
					};

					var updatedInvoiceResult = _restService.Patch<Invoice, InvoiceDisablePayments>(invoice.url, disablePaymentInvoice);
					if (updatedInvoiceResult.IsSuccessStatusCode)
					{
						updatedInvoices.Add(updatedInvoiceResult.Result);
						messages.Add($"Disabled payments on invoice_number: {invoice.invoice_number}");
					}
					else
					{
						throw new InvalidOperationException("Failed to save disable_payments = true");
					}
				});
			}
			else
			{
				throw new InvalidOperationException("Failed to retrieve invoice list");
			}

			ViewBag.Message = string.Join(Environment.NewLine, messages);
			return View(updatedInvoices);
		}

		public ActionResult ChargeLateFees(double lateRate = 0.02)
		{
			var defaultLatePercentRate = new decimal(lateRate);
			var catchupOnLatefees = false;
			List<Invoice> addedFeeInvoices = new List<Invoice>();

			var overdueInvoices = _restService.Get<List<Invoice>>(
				$"https://api.waveapps.com/businesses/{_appAppSettings.MahayagBusinessGuid}/invoices/?status=overdue&embed_customer=true");
			

			//TODO: handle multiple invoices per single customer
			foreach (var invoice in overdueInvoices.Result)
			{
				var custSettings = _customerSettingsService.ExctractFromCustomerObject(invoice.customer);

				if (custSettings?.ChargeLateFee != null && custSettings.ChargeLateFee.Value && 
					custSettings.NextLateFeeChargeDate != null && custSettings.NextLateFeeChargeDate.Value.Date <= DateTime.Now.Date 
				    && custSettings.LateFeeChargeAboveBalance <= invoice.invoice_amount_due)
				{
					#region add late fee
					var lateFee = new InvoiceItem
					{
						product = new Product{ id = _appAppSettings.MahayagLateFeeProductId },
						description = $"Late Charge: {100 * (custSettings.LateFeePercentRate?? defaultLatePercentRate)}% " +
						              $"from PastDueAmount: {invoice.invoice_amount_due} " +
						              $"as of date: {custSettings.NextLateFeeChargeDate.Value.Date.ToShortDateString()} " +
						              $"added on: {DateTime.Now.ToShortDateString()}",
						quantity = 1,
						price = invoice.invoice_amount_due * (custSettings.LateFeePercentRate ?? defaultLatePercentRate)
					};

					var addResult = _restService.Post<AddInvoiceItemResponse, InvoiceItem>(invoice.items_url, lateFee);

					if (addResult.IsSuccessStatusCode)
					{
						addedFeeInvoices.Add(invoice);

						#region update NextLateFeeChargeDate

						custSettings.NextLateFeeChargeDate = custSettings.NextLateFeeChargeDate.Value.AddDays(1).Date;

						var updatedCustomerResult =
							_customerSettingsService.SaveUpdatedCustomerSettings(invoice.customer.url, custSettings, _restService);

						if (updatedCustomerResult.IsSuccessStatusCode == false)
						{
							throw new InvalidOperationException("Saving NextLateFeeChargeDate failed");
						}

						#endregion
					}
					else
					{
						throw new InvalidOperationException("add lateFee failed");
					}
					#endregion
					
					//determine if process needs to catch up on late fees
					if (custSettings.NextLateFeeChargeDate.Value <= DateTime.Today)
						catchupOnLatefees = true;
				}
			}

			if (catchupOnLatefees)
			{
				//recusrsive call to catch up on late fees if process has not run for more than a day
				ChargeLateFees((double)defaultLatePercentRate);
			}

			return View(addedFeeInvoices);
		}

		private class AddInvoiceItemResponse
		{
		}

		private class UpdateInvoiceItemResponse
		{
		}

		private class UpdateCustomerResult
		{
		}
	}
}
